﻿using System;
using System.Buffers;
using System.Threading.Tasks;
using System.IO.Pipelines;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuperSocket.ProtoBase;
using System.Collections;
using System.Threading.Tasks.Sources;
using System.Threading;

namespace SuperSocket.Channel
{
    public abstract partial class PipeChannel<TPackageInfo> : ChannelBase<TPackageInfo>, IChannel<TPackageInfo>, IChannel, IPipeChannel
        where TPackageInfo : class
    {
        private IPipelineFilter<TPackageInfo> _pipelineFilter;

        protected Pipe Out { get; }

        Pipe IPipeChannel.Out
        {
            get { return Out; }
        }

        protected Pipe In { get; }

        Pipe IPipeChannel.In
        {
            get { return In; }
        }

        protected ILogger Logger { get; }

        protected ChannelOptions Options { get; }

        protected PipeChannel(IPipelineFilter<TPackageInfo> pipelineFilter, ChannelOptions options)
        {
            _pipelineFilter = pipelineFilter;
            _packageTaskSource = new ManualResetValueTaskSourceCore<TPackageInfo>();
            Options = options;
            Logger = options.Logger;
            Out = options.Out ?? new Pipe();
            In = options.In ?? new Pipe();
        }

        public override async Task StartAsync()
        {
            try
            {
                var processTask = ProcessPackages();
                var readsTask = ProcessReads();
                var sendsTask = ProcessSends();                

                await Task.WhenAll(readsTask, sendsTask, processTask);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Unhandled exception in the method PipeChannel.StartAsync.");
            }
            finally
            {
                OnClosed();
            }
        }

        private async Task ProcessPackages()
        {
            while (true)
            {
                var package = await ReceivePackage();

                if (package == null)
                    break;

                await OnPackageReceived(package);
            }
        }

        protected virtual async Task FillPipeAsync(PipeWriter writer)
        {
            var options = Options;

            while (true)
            {
                try
                {
                    var bufferSize = options.ReceiveBufferSize;
                    var maxPackageLength = options.MaxPackageLength;

                    if (maxPackageLength > 0)
                        bufferSize = Math.Min(bufferSize, maxPackageLength);

                    var memory = writer.GetMemory(bufferSize);

                    var bytesRead = await FillPipeWithDataAsync(memory);         

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    // Tell the PipeWriter how much was read
                    writer.Advance(bytesRead);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Exception happened in ReceiveAsync");
                    break;
                }

                // Make the data available to the PipeReader
                var result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Signal to the reader that we're done writing
            writer.Complete();
            Out.Writer.Complete();// TODO: should complete the output right now?
        }

        protected abstract ValueTask<int> FillPipeWithDataAsync(Memory<byte> memory);

        protected virtual async Task ProcessReads()
        {
            var pipe = In;

            Task writing = FillPipeAsync(pipe.Writer);
            Task reading = ReadPipeAsync(pipe.Reader);

            await Task.WhenAll(reading, writing);
        }

        protected async Task ProcessSends()
        {
            var output = Out.Reader;

            while (true)
            {
                var result = await output.ReadAsync();

                if (result.IsCanceled)
                    break;

                var completed = result.IsCompleted;

                var buffer = result.Buffer;
                var end = buffer.End;
                
                if (!buffer.IsEmpty)
                {
                    try
                    {
                        await SendAsync(buffer);
                    }
                    catch (Exception e)
                    {
                        Logger.LogError(e, "Exception happened in SendAsync");
                        output.Complete(e);
                        return;
                    }
                }

                output.AdvanceTo(end);

                if (completed)
                {
                    break;
                }
            }

            output.Complete();
        }

        protected abstract ValueTask<int> SendAsync(ReadOnlySequence<byte> buffer);


        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer)
        {
            var writer = Out.Writer;
            await writer.WriteAsync(buffer);
            await writer.FlushAsync();
        }

        public override async ValueTask SendAsync<TPackage>(IPackageEncoder<TPackage> packageEncoder, TPackage package)
        {
            var writer = Out.Writer;
            packageEncoder.Encode(writer, package);
            await writer.FlushAsync();
        }

        protected internal ArraySegment<T> GetArrayByMemory<T>(ReadOnlyMemory<T> memory)
        {
            if (!MemoryMarshal.TryGetArray(memory, out var result))
            {
                throw new InvalidOperationException("Buffer backed by array was expected");
            }

            return result;
        }

        protected async Task ReadPipeAsync(PipeReader reader)
        {
            while (true)
            {
                var result = await reader.ReadAsync();

                var buffer = result.Buffer;

                SequencePosition consumed = buffer.Start;
                SequencePosition examined = buffer.End;

                try
                {
                    if (result.IsCanceled)
                        break;

                    var completed = result.IsCompleted;

                    if (buffer.Length > 0)
                    {
                        if (!ReaderBuffer(buffer, out consumed, out examined))
                        {
                            completed = true;
                            break;
                        }                        
                    }

                    if (completed)
                        break;
                }
                finally
                {
                    reader.AdvanceTo(consumed, examined);
                }
            }

            reader.Complete();
        }

        private bool ReaderBuffer(ReadOnlySequence<byte> buffer, out SequencePosition consumed, out SequencePosition examined)
        {
            consumed = buffer.Start;
            examined = buffer.End;

            var maxPackageLength = Options.MaxPackageLength;

            var seqReader = new SequenceReader<byte>(buffer);

            while (true)
            {
                var currentPipelineFilter = _pipelineFilter;

                var packageInfo = currentPipelineFilter.Filter(ref seqReader);

                if (currentPipelineFilter.NextFilter != null)
                    _pipelineFilter = currentPipelineFilter.NextFilter;

                var pos = seqReader.Position.GetInteger();

                if (maxPackageLength > 0 && pos > maxPackageLength)
                {
                    Logger.LogError($"Package cannot be larger than {maxPackageLength}.");
                    // close the the connection directly
                    Close();
                    _packageTaskSource.SetResult(null);
                    return false;
                }
            
                // continue receive...
                if (packageInfo == null)
                {
                    return true;
                }

                currentPipelineFilter.Reset();

                _packageTaskSource.SetResult(packageInfo);
                _packageTaskSource.Reset();        

                if (seqReader.End) // no more data
                {
                    consumed = buffer.End;
                    break;
                }
                else
                {
                    examined = consumed = seqReader.Position;
                }
            }

            return true;
        }
    }
}
