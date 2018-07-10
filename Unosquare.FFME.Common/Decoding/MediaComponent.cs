﻿namespace Unosquare.FFME.Decoding
{
    using Core;
    using FFmpeg.AutoGen;
    using Primitives;
    using Shared;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Runtime.CompilerServices;

    /// <summary>
    /// Represents a media component of a given media type within a
    /// media container. Derived classes must implement frame handling
    /// logic.
    /// </summary>
    /// <seealso cref="IDisposable" />
    internal abstract unsafe class MediaComponent : IDisposable
    {
        #region Private Declarations

#pragma warning disable SA1401 // Field must be private

        /// <summary>
        /// Holds a reference to the Codec Context.
        /// </summary>
        internal AVCodecContext* CodecContext;

        /// <summary>
        /// Holds a reference to the associated input context stream
        /// </summary>
        internal AVStream* Stream;

#pragma warning restore SA1401 // Field must be private

        /// <summary>
        /// Related to issue 94, looks like FFmpeg requires exclusive access when calling avcodec_open2()
        /// </summary>
        private static readonly object CodecLock = new object();

        /// <summary>
        /// Contains the packets pending to be sent to the decoder
        /// </summary>
        private readonly PacketQueue Packets = new PacketQueue();

        /// <summary>
        /// The decode packet function
        /// </summary>
        private readonly Func<MediaFrame> DecodePacketFunction;

        /// <summary>
        /// Detects redundant, unmanaged calls to the Dispose method.
        /// </summary>
        private AtomicBoolean m_IsDisposed = new AtomicBoolean(false);

        /// <summary>
        /// Determines if packets have been fed into the codec and frames can be decoded.
        /// </summary>
        private AtomicBoolean m_HasCodecPackets = new AtomicBoolean(false);

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="MediaComponent"/> class.
        /// </summary>
        /// <param name="container">The container.</param>
        /// <param name="streamIndex">Index of the stream.</param>
        /// <exception cref="ArgumentNullException">container</exception>
        /// <exception cref="MediaContainerException">The container exception.</exception>
        protected MediaComponent(MediaContainer container, int streamIndex)
        {
            // Ported from: https://github.com/FFmpeg/FFmpeg/blob/master/fftools/ffplay.c#L2559
            // avctx = avcodec_alloc_context3(NULL);
            Container = container ?? throw new ArgumentNullException(nameof(container));
            CodecContext = ffmpeg.avcodec_alloc_context3(null);
            RC.Current.Add(CodecContext, $"134: {nameof(MediaComponent)}[{MediaType}].ctor()");
            StreamIndex = streamIndex;
            Stream = container.InputContext->streams[StreamIndex];
            StreamInfo = container.MediaInfo.Streams[StreamIndex];

            // Set default codec context options from probed stream
            var setCodecParamsResult = ffmpeg.avcodec_parameters_to_context(CodecContext, Stream->codecpar);

            if (setCodecParamsResult < 0)
                Container.Parent?.Log(MediaLogMessageType.Warning, $"Could not set codec parameters. Error code: {setCodecParamsResult}");

            // We set the packet timebase in the same timebase as the stream as opposed to the tpyical AV_TIME_BASE
            if (this is VideoComponent && Container.MediaOptions.VideoForcedFps > 0)
            {
                var fpsRational = ffmpeg.av_d2q(Container.MediaOptions.VideoForcedFps, 1000000);
                Stream->r_frame_rate = fpsRational;
                CodecContext->pkt_timebase = new AVRational { num = fpsRational.den, den = fpsRational.num };
            }
            else
            {
                CodecContext->pkt_timebase = Stream->time_base;
            }

            // Find the default decoder codec from the stream and set it.
            var defaultCodec = ffmpeg.avcodec_find_decoder(Stream->codec->codec_id);
            AVCodec* forcedCodec = null;

            // If set, change the codec to the forced codec.
            if (Container.MediaOptions.DecoderCodec.ContainsKey(StreamIndex) &&
                string.IsNullOrWhiteSpace(Container.MediaOptions.DecoderCodec[StreamIndex]) == false)
            {
                var forcedCodecName = Container.MediaOptions.DecoderCodec[StreamIndex];
                forcedCodec = ffmpeg.avcodec_find_decoder_by_name(forcedCodecName);
                if (forcedCodec == null)
                {
                    Container.Parent?.Log(MediaLogMessageType.Warning,
                        $"COMP {MediaType.ToString().ToUpperInvariant()}: Unable to set decoder codec to '{forcedCodecName}' on stream index {StreamIndex}");
                }
            }

            // Check we have a valid codec to open and process the stream.
            if (defaultCodec == null && forcedCodec == null)
            {
                var errorMessage = $"Fatal error. Unable to find suitable decoder for {Stream->codec->codec_id.ToString()}";
                CloseComponent();
                throw new MediaContainerException(errorMessage);
            }

            var codecCandidates = new AVCodec*[] { forcedCodec, defaultCodec };
            AVCodec* selectedCodec = null;
            var codecOpenResult = 0;

            foreach (var codec in codecCandidates)
            {
                if (codec == null)
                    continue;

                // Pass default codec stuff to the codec contect
                CodecContext->codec_id = codec->id;

                // Legacy code from ffplay.c (v < 4.0) befor the send packet/receive frame logic, this used to be required.
                // Now it only corrupts frame decoding. See issue #251
                // if ((codec->capabilities & ffmpeg.AV_CODEC_CAP_TRUNCATED) != 0) CodecContext->flags |= ffmpeg.AV_CODEC_FLAG_TRUNCATED;
                // if ((codec->capabilities & ffmpeg.AV_CODEC_FLAG2_CHUNKS) != 0) CodecContext->flags |= ffmpeg.AV_CODEC_FLAG2_CHUNKS;

                // Process the decoder options
                {
                    var decoderOptions = Container.MediaOptions.DecoderParams;

                    // Configure the codec context flags
                    if (decoderOptions.EnableFastDecoding) CodecContext->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
                    if (decoderOptions.EnableLowDelayDecoding) CodecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

                    // process the low res option
                    if (decoderOptions.LowResolutionIndex != ResolutionDivider.Full && codec->max_lowres > 0)
                    {
                        var lowResOption = Math.Min((byte)decoderOptions.LowResolutionIndex, codec->max_lowres)
                            .ToString(CultureInfo.InvariantCulture);
                        decoderOptions.LowResIndexOption = lowResOption;
                    }

                    // Ensure ref counted frames for audio and video decoding
                    if (CodecContext->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO || CodecContext->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                        decoderOptions.RefCountedFrames = "1";
                }

                // Setup additional settings. The most important one is Threads -- Setting it to 1 decoding is very slow. Setting it to auto
                // decoding is very fast in most scenarios.
                var codecOptions = Container.MediaOptions.DecoderParams.GetStreamCodecOptions(Stream->index);

                // Enable Hardware acceleration if requested
                if (this is VideoComponent && container.MediaOptions.VideoHardwareDevice != null)
                    HardwareAccelerator.Attach(this as VideoComponent, container.MediaOptions.VideoHardwareDevice);

                // Open the CodecContext. This requires exclusive FFmpeg access
                lock (CodecLock)
                {
                    fixed (AVDictionary** codecOptionsRef = &codecOptions.Pointer)
                        codecOpenResult = ffmpeg.avcodec_open2(CodecContext, codec, codecOptionsRef);
                }

                // Check if the codec opened successfully
                if (codecOpenResult < 0)
                {
                    Container.Parent?.Log(MediaLogMessageType.Warning,
                        $"Unable to open codec '{FFInterop.PtrToStringUTF8(codec->name)}' on stream {streamIndex}");

                    continue;
                }

                // If there are any codec options left over from passing them, it means they were not consumed
                var currentEntry = codecOptions.First();
                while (currentEntry != null && currentEntry?.Key != null)
                {
                    Container.Parent?.Log(MediaLogMessageType.Warning,
                        $"Invalid codec option: '{currentEntry.Key}' for codec '{FFInterop.PtrToStringUTF8(codec->name)}', stream {streamIndex}");
                    currentEntry = codecOptions.Next(currentEntry);
                }

                selectedCodec = codec;
                break;
            }

            if (selectedCodec == null)
            {
                CloseComponent();
                throw new MediaContainerException($"Unable to find suitable decoder codec for stream {streamIndex}. Error code {codecOpenResult}");
            }

            // Startup done. Set some options.
            Stream->discard = AVDiscard.AVDISCARD_DEFAULT;
            MediaType = (MediaType)CodecContext->codec_type;

            switch (MediaType)
            {
                case MediaType.Audio:
                case MediaType.Video:
                    DecodePacketFunction = DecodeNextAVFrame;
                    break;
                case MediaType.Subtitle:
                    DecodePacketFunction = DecodeNextAVSubtitle;
                    break;
                default:
                    throw new NotSupportedException($"A compoenent of MediaType '{MediaType}' is not supported");
            }

            // Compute the start time
            if (Stream->start_time == ffmpeg.AV_NOPTS_VALUE)
                StartTimeOffset = Container.MediaStartTimeOffset;
            else
                StartTimeOffset = Stream->start_time.ToTimeSpan(Stream->time_base);

            // compute the duration
            if (Stream->duration == ffmpeg.AV_NOPTS_VALUE || Stream->duration == 0)
                Duration = Container.InputContext->duration.ToTimeSpan();
            else
                Duration = Stream->duration.ToTimeSpan(Stream->time_base);

            CodecId = Stream->codec->codec_id;
            CodecName = FFInterop.PtrToStringUTF8(selectedCodec->name);
            Bitrate = Stream->codec->bit_rate < 0 ? 0 : Convert.ToUInt64(Stream->codec->bit_rate);
            Container.Parent?.Log(MediaLogMessageType.Debug,
                $"COMP {MediaType.ToString().ToUpperInvariant()}: Start Offset: {StartTimeOffset.Format()}; Duration: {Duration.Format()}");

            // Begin processing with a flush packet
            SendFlushPacket();
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the media container associated with this component.
        /// </summary>
        public MediaContainer Container { get; }

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        public MediaType MediaType { get; }

        /// <summary>
        /// Gets the index of the associated stream.
        /// </summary>
        public int StreamIndex { get; }

        /// <summary>
        /// Gets the component's stream start timestamp as reported
        /// by the start time of the stream.
        /// </summary>
        public TimeSpan StartTimeOffset { get; }

        /// <summary>
        /// Gets the duration of this stream component.
        /// If there is no such information it will return TimeSpan.MinValue
        /// </summary>
        public TimeSpan Duration { get; }

        /// <summary>
        /// Gets the current length in bytes of the
        /// packet buffer. Limit your Reads to something reasonable before
        /// this becomes too large.
        /// </summary>
        public ulong PacketBufferLength => Packets.BufferLength;

        /// <summary>
        /// Gets the number of packets in the queue.
        /// Decode packets until this number becomes 0.
        /// </summary>
        public int PacketBufferCount => Packets.Count;

        /// <summary>
        /// Gets the total amount of bytes read by this component in the lifetime of this component.
        /// </summary>
        public ulong LifetimeBytesRead { get; private set; } = 0;

        /// <summary>
        /// Gets the ID of the codec for this component.
        /// </summary>
        public AVCodecID CodecId { get; }

        /// <summary>
        /// Gets the name of the codec for this component.
        /// </summary>
        public string CodecName { get; }

        /// <summary>
        /// Gets the bitrate of this component as reported by the codec context.
        /// Returns 0 for unknown.
        /// </summary>
        public ulong Bitrate { get; }

        /// <summary>
        /// Gets the stream information.
        /// </summary>
        public StreamInfo StreamInfo { get; }

        /// <summary>
        /// Gets whether packets have been fed into the codec and frames can be decoded.
        /// </summary>
        public bool HasCodecPackets
        {
            get => m_HasCodecPackets.Value;
            private set => m_HasCodecPackets.Value = value;
        }

        /// <summary>
        /// Gets a value indicating whether this instance is disposed.
        /// </summary>
        public bool IsDisposed
        {
            get => m_IsDisposed.Value;
            private set => m_IsDisposed.Value = value;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Clears the pending and sent Packet Queues releasing all memory held by those packets.
        /// Additionally it flushes the codec buffered packets.
        /// </summary>
        /// <param name="flushBuffers">if set to <c>true</c> flush codec buffers.</param>
        public void ClearQueuedPackets(bool flushBuffers)
        {
            // Release packets that are already in the queue.
            Packets.Clear();

            if (flushBuffers)
                FlushCodecBuffers();
        }

        /// <summary>
        /// Sends a special kind of packet (an empty/null packet)
        /// that tells the decoder to refresh the attached picture or enter draining mode.
        /// This is a port of packet_queue_put_nullpacket
        /// </summary>
        public void SendEmptyPacket()
        {
            var packet = PacketQueue.CreateEmptyPacket(Stream->index);
            SendPacket(packet);
        }

        /// <summary>
        /// Pushes a packet into the decoding Packet Queue
        /// and processes the packet in order to try to decode
        /// 1 or more frames.
        /// </summary>
        /// <param name="packet">The packet.</param>
        public void SendPacket(AVPacket* packet)
        {
            if (packet == null)
            {
                SendEmptyPacket();
                return;
            }

            if (packet->size > 0)
                LifetimeBytesRead += (ulong)packet->size;

            Packets.Push(packet);
        }

        /// <summary>
        /// Feeds the decoder buffer and tries to return the next available frame.
        /// </summary>
        /// <returns>The received Media Frame. It is null if no frame could be retrieved.</returns>
        public MediaFrame ReceiveNextFrame() =>
            DecodePacketFunction();

        /// <summary>
        /// Converts decoded, raw frame data in the frame source into a a usable frame. <br />
        /// The process includes performing picture, samples or text conversions
        /// so that the decoded source frame data is easily usable in multimedia applications
        /// </summary>
        /// <param name="input">The source frame to use as an input.</param>
        /// <param name="output">The target frame that will be updated with the source frame. If null is passed the frame will be instantiated.</param>
        /// <param name="siblings">The sibling blocks that may help guess some additional parameters for the input frame.</param>
        /// <returns>
        /// Returns true of the operation succeeded. False otherwise.
        /// </returns>
        public abstract bool MaterializeFrame(MediaFrame input, ref MediaBlock output, List<MediaBlock> siblings);

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose() => Dispose(true);

        /// <summary>
        /// Determines whether the specified packet is a flush packet.
        /// These flush packets are used to clear the internal decoder buffers
        /// </summary>
        /// <param name="packet">The packet to check.</param>
        /// <returns>
        ///   <c>true</c> if it a flush packet<c>false</c>.
        /// </returns>
        protected static bool IsFlushPacket(AVPacket* packet)
        {
            if (packet == null) return false;
            return (IntPtr)packet->data == PacketQueue.FlushPacketData;
        }

        /// <summary>
        /// Creates a frame source object given the raw FFmpeg AVFrame or AVSubtitle reference.
        /// </summary>
        /// <param name="framePointer">The raw FFmpeg pointer.</param>
        /// <returns>The media frame</returns>
        protected abstract MediaFrame CreateFrameSource(IntPtr framePointer);

        /// <summary>
        /// Releases the existing codec context and clears and disposes the packet queues.
        /// </summary>
        protected void CloseComponent()
        {
            if (CodecContext != null)
            {
                RC.Current.Remove(CodecContext);
                fixed (AVCodecContext** codecContext = &CodecContext)
                    ffmpeg.avcodec_free_context(codecContext);

                // free all the pending and sent packets
                ClearQueuedPackets(true);
                Packets.Dispose();
            }

            CodecContext = null;
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool alsoManaged)
        {
            lock (CodecLock)
            {
                if (IsDisposed) return;
                CloseComponent();
                IsDisposed = true;
            }
        }

        /// <summary>
        /// Sends a special kind of packet (a flush packet)
        /// that tells the decoder to flush it internal buffers
        /// This an encapsulation of flush_pkt
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendFlushPacket()
        {
            var packet = PacketQueue.CreateFlushPacket(Stream->index);
            SendPacket(packet);
        }

        /// <summary>
        /// Flushes the codec buffers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushCodecBuffers()
        {
            if (CodecContext != null)
                ffmpeg.avcodec_flush_buffers(CodecContext);

            HasCodecPackets = false;
        }

        /// <summary>
        /// Feeds the packets to decoder.
        /// </summary>
        /// <param name="fillDecoderBuffer">if set to <c>true</c> fills the decoder buffer with packets.</param>
        /// <returns>The number of packets fed</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int FeedPacketsToDecoder(bool fillDecoderBuffer)
        {
            var packetCount = 0;
            var sendPacketResult = 0;

            while (Packets.Count > 0)
            {
                var packet = Packets.Peek();
                if (IsFlushPacket(packet))
                {
                    FlushCodecBuffers();
                    packet = Packets.Dequeue();
                    PacketQueue.ReleasePacket(packet);
                    continue;
                }

                sendPacketResult = ffmpeg.avcodec_send_packet(CodecContext, packet);

                // EAGAIN means we have filled the decoder buffer
                if (sendPacketResult != -ffmpeg.EAGAIN)
                {
                    packet = Packets.Dequeue();
                    PacketQueue.ReleasePacket(packet);
                    packetCount++;
                }

                if (sendPacketResult >= 0)
                    HasCodecPackets = true;

                if (fillDecoderBuffer && sendPacketResult >= 0)
                    continue;

                break;
            }

            return packetCount;
        }

        /// <summary>
        /// Receives the next available frame from decoder.
        /// </summary>
        /// <param name="receiveFrameResult">The receive frame result.</param>
        /// <returns>The frame or null if no frames could be decoded</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MediaFrame ReceiveFrameFromDecoder(out int receiveFrameResult)
        {
            var managedFrame = default(MediaFrame);
            receiveFrameResult = 0;

            var outputFrame = MediaFrame.CreateAVFrame();
            managedFrame = null;
            receiveFrameResult = ffmpeg.avcodec_receive_frame(CodecContext, outputFrame);

            if (receiveFrameResult >= 0)
                managedFrame = CreateFrameSource(new IntPtr(outputFrame));

            if (managedFrame == null)
                MediaFrame.ReleaseAVFrame(outputFrame);

            if (receiveFrameResult == ffmpeg.AVERROR_EOF)
                FlushCodecBuffers();

            if (receiveFrameResult == -ffmpeg.EAGAIN)
                HasCodecPackets = false;

            return managedFrame;
        }

        /// <summary>
        /// Decodes the next Audio or Video frame.
        /// Reference: https://www.ffmpeg.org/doxygen/4.0/group__lavc__encdec.html
        /// </summary>
        /// <returns>A deocder result containing the decoder frames (if any)</returns>
        private MediaFrame DecodeNextAVFrame()
        {
            var frame = ReceiveFrameFromDecoder(out var receiveFrameResult);
            if (frame == null)
            {
                FeedPacketsToDecoder(false);
                frame = ReceiveFrameFromDecoder(out receiveFrameResult);
            }

            while (frame == null && FeedPacketsToDecoder(true) > 0)
            {
                frame = ReceiveFrameFromDecoder(out receiveFrameResult);
                if (receiveFrameResult < 0)
                    break;
            }

            if (frame != null && Container.Components.OnFrameDecoded != null)
            {
                if (MediaType == MediaType.Audio)
                    Container.Components.OnFrameDecoded?.Invoke((IntPtr)(frame as AudioFrame).Pointer, MediaType);
                else if (MediaType == MediaType.Video)
                    Container.Components.OnFrameDecoded?.Invoke((IntPtr)(frame as VideoFrame).Pointer, MediaType);
            }

            return frame;
        }

        /// <summary>
        /// Decodes the next subtitle frame.
        /// </summary>
        /// <returns>The managed frame</returns>
        private MediaFrame DecodeNextAVSubtitle()
        {
            // For subtitles we use the old API (new API send_packet/receive_frame) is not yet available
            // We first try to flush anything we've already sent by using an empty packet.
            var managedFrame = default(MediaFrame);
            var packet = PacketQueue.CreateEmptyPacket(Stream->index);
            var gotFrame = 0;
            var outputFrame = MediaFrame.CreateAVSubtitle();
            var receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, outputFrame, &gotFrame, packet);

            // If we don't get a frame from flushing. Feed the packet into the decoder and try getting a frame.
            if (gotFrame == 0)
            {
                PacketQueue.ReleasePacket(packet);
                packet = Packets.Dequeue();
                if (packet != null)
                    receiveFrameResult = ffmpeg.avcodec_decode_subtitle2(CodecContext, outputFrame, &gotFrame, packet);
            }

            // If we got a frame, turn into a managed frame
            if (gotFrame != 0)
            {
                Container.Components.OnSubtitleDecoded?.Invoke((IntPtr)outputFrame);
                managedFrame = CreateFrameSource((IntPtr)outputFrame);
            }

            // Free the packet if we have allocated it
            if (packet != null)
                PacketQueue.ReleasePacket(packet);

            // deallocate the subtitle frame if we did not associate it with a managed frame.
            if (managedFrame == null)
                MediaFrame.ReleaseAVSubtitle(outputFrame);

            if (receiveFrameResult < 0)
                HasCodecPackets = false;

            return managedFrame;
        }

        #endregion
    }
}
