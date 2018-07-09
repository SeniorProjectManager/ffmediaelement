﻿namespace Unosquare.FFME
{
    using Core;
    using Primitives;
    using Shared;
    using System;
    using System.Runtime.CompilerServices;
    using System.Threading;

    public partial class MediaEngine
    {
        /// <summary>
        /// This partial class implements:
        /// 1. Packet reading from the Container
        /// 2. Frame Decoding from packet buffer and Block buffering
        /// 3. Block Rendering from block buffer
        /// </summary>

        #region State Management

        private Thread PacketReadingTask = null;
        private Thread FrameDecodingTask = null;
        private Timer BlockRenderingWorker = null;
        private IWaitEvent BlockRenderingWorkerExit = null;

        /// <summary>
        /// Holds the materialized block cache for each media type.
        /// </summary>
        public MediaTypeDictionary<MediaBlockBuffer> Blocks { get; } = new MediaTypeDictionary<MediaBlockBuffer>();

        /// <summary>
        /// Gets the preloaded subtitle blocks.
        /// </summary>
        public MediaBlockBuffer PreloadedSubtitles { get; private set; } = null;

        /// <summary>
        /// Gets the packet reading cycle control evenet.
        /// </summary>
        internal IWaitEvent PacketReadingCycle { get; } = WaitEventFactory.Create(isCompleted: false, useSlim: true);

        /// <summary>
        /// Gets the frame decoding cycle control event.
        /// </summary>
        internal IWaitEvent FrameDecodingCycle { get; } = WaitEventFactory.Create(isCompleted: false, useSlim: true);

        /// <summary>
        /// Gets the block rendering cycle control event.
        /// </summary>
        internal IWaitEvent BlockRenderingCycle { get; } = WaitEventFactory.Create(isCompleted: false, useSlim: true);

        /// <summary>
        /// Holds the block renderers
        /// </summary>
        internal MediaTypeDictionary<IMediaRenderer> Renderers { get; } = new MediaTypeDictionary<IMediaRenderer>();

        /// <summary>
        /// Holds the last rendered StartTime for each of the media block types
        /// </summary>
        internal MediaTypeDictionary<TimeSpan> LastRenderTime { get; } = new MediaTypeDictionary<TimeSpan>();

        /// <summary>
        /// Gets a value indicating whether more packets can be read from the stream.
        /// This does not check if the packet queue is full.
        /// </summary>
        internal bool CanReadMorePackets => (Container?.IsReadAborted ?? true) == false
            && (Container?.IsAtEndOfStream ?? true) == false;

        /// <summary>
        /// Gets a value indicating whether room is available in the download cache.
        /// </summary>
        internal bool ShouldReadMorePackets
        {
            get
            {
                if (Commands.IsStopWorkersPending || Container == null || Container.Components == null)
                    return false;

                // If it's a live stream always continue reading regardless
                if (State.IsLiveStream) return true;

                return Container.Components.PacketBufferLength < State.DownloadCacheLength;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Initializes the media block buffers and
        /// starts packet reader, frame decoder, and block rendering workers.
        /// </summary>
        internal void StartWorkers()
        {
            // Initialize the block buffers
            foreach (var t in Container.Components.MediaTypes)
            {
                Blocks[t] = new MediaBlockBuffer(Constants.MaxBlocks[t], t);
                Renderers[t] = Platform.CreateRenderer(t, this);
                InvalidateRenderer(t);
            }

            // Create the renderer for the preloaded subs
            if (PreloadedSubtitles != null)
            {
                Renderers[PreloadedSubtitles.MediaType] = Platform.CreateRenderer(PreloadedSubtitles.MediaType, this);
                InvalidateRenderer(PreloadedSubtitles.MediaType);
            }

            Clock.SpeedRatio = Constants.Controller.DefaultSpeedRatio;
            Commands.IsStopWorkersPending = false;

            // Set the initial state of the task cycles.
            BlockRenderingCycle.Complete();
            FrameDecodingCycle.Begin();
            PacketReadingCycle.Begin();

            // Create the thread runners
            PacketReadingTask = new Thread(RunPacketReadingWorker)
            { IsBackground = true, Name = nameof(PacketReadingTask), Priority = ThreadPriority.Normal };

            FrameDecodingTask = new Thread(RunFrameDecodingWorker)
            { IsBackground = true, Name = nameof(FrameDecodingTask), Priority = ThreadPriority.AboveNormal };

            // Fire up the threads
            PacketReadingTask.Start();
            FrameDecodingTask.Start();
            StartBlockRenderingWorker();
        }

        /// <summary>
        /// Stops the packet reader, frame decoder, and block renderers
        /// </summary>
        internal void StopWorkers()
        {
            // Pause the clock so no further updates are propagated
            Clock.Pause();

            // Let the threads know a cancellation is pending.
            Commands.IsStopWorkersPending = true;

            // Cause an immediate Packet read abort
            Container?.SignalAbortReads(false);

            // Stop the rendering worker before anything else
            StopBlockRenderingWorker();

            // Call close on all renderers
            foreach (var renderer in Renderers.Values)
                renderer.Close();

            // Stop the rest of the workers
            // i.e. wait for worker threads to finish
            var wrokers = new[] { PacketReadingTask, FrameDecodingTask };
            foreach (var w in wrokers)
            {
                // Abort causes memory leaks bacause packets and frames might not
                // get disposed by the corresponding workers. We use Join instead.
                // w.Abort();
                w?.Join();
            }

            // Set the threads to null
            FrameDecodingTask = null;
            PacketReadingTask = null;

            // Remove the renderers disposing of them
            Renderers.Clear();

            // Reset the clock
            Clock.Reset();
        }

        /// <summary>
        /// Preloads the subtitles from the MediaOptions.SubtitlesUrl.
        /// </summary>
        internal void PreloadSubtitles()
        {
            DisposePreloadedSubtitles();
            var subtitlesUrl = Container.MediaOptions.SubtitlesUrl;

            // Don't load a thing if we don't have to
            if (string.IsNullOrWhiteSpace(subtitlesUrl))
                return;

            try
            {
                PreloadedSubtitles = LoadBlocks(subtitlesUrl, MediaType.Subtitle, this);

                // Process and adjust subtitle delays if necessary
                if (Container.MediaOptions.SubtitlesDelay != TimeSpan.Zero)
                {
                    var delay = Container.MediaOptions.SubtitlesDelay;
                    for (var i = 0; i < PreloadedSubtitles.Count; i++)
                    {
                        var target = PreloadedSubtitles[i];
                        target.StartTime = TimeSpan.FromTicks(target.StartTime.Ticks + delay.Ticks);
                        target.EndTime = TimeSpan.FromTicks(target.EndTime.Ticks + delay.Ticks);
                        target.Duration = TimeSpan.FromTicks(target.EndTime.Ticks - target.StartTime.Ticks);
                    }
                }

                Container.MediaOptions.IsSubtitleDisabled = true;
            }
            catch (MediaContainerException mex)
            {
                DisposePreloadedSubtitles();
                Log(MediaLogMessageType.Warning,
                    $"No subtitles to side-load found in media '{subtitlesUrl}'. {mex.Message}");
            }
        }

        /// <summary>
        /// Returns the value of a discrete frame position of themain media component if possible.
        /// Otherwise, it simply rounds the position to the nearest millisecond.
        /// </summary>
        /// <param name="position">The position.</param>
        /// <returns>The snapped, discrete, normalized position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan SnapPositionToBlockPosition(TimeSpan position)
        {
            // return position;
            if (Container == null)
                return position.Normalize();

            var blocks = Blocks[Container.Components.Main.MediaType];
            if (blocks == null) return position.Normalize();

            return blocks.GetSnapPosition(position) ?? position.Normalize();
        }

        /// <summary>
        /// Gets a value indicating whether more frames can be converted into blocks of the given type.
        /// </summary>
        /// <param name="t">The t.</param>
        /// <returns>
        ///   <c>true</c> if this instance [can read more frames of] the specified t; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanReadMoreFramesOf(MediaType t)
        {
            return CanReadMorePackets ||
                Container.Components[t].PacketBufferLength > 0 ||
                Container.Components[t].HasCodecPackets;
        }

        /// <summary>
        /// Sends the given block to its corresponding media renderer.
        /// </summary>
        /// <param name="block">The block.</param>
        /// <param name="clockPosition">The clock position.</param>
        /// <returns>The number of blocks sent to the renderer</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SendBlockToRenderer(MediaBlock block, TimeSpan clockPosition)
        {
            // Process property changes coming from video blocks
            if (block.MediaType == MediaType.Video)
            {
                if (block is VideoBlock videoBlock)
                {
                    State.VideoSmtpeTimecode = videoBlock.SmtpeTimecode;
                    State.VideoHardwareDecoder = (Container?.Components.Video?.IsUsingHardwareDecoding ?? false) ?
                        Container?.Components.Video?.HardwareAccelerator?.Name ?? string.Empty : string.Empty;
                }
            }

            // Send the block to its corresponding renderer
            Renderers[block.MediaType]?.Render(block, clockPosition);
            LastRenderTime[block.MediaType] = block.StartTime;

            // Extension method for logging
            var blockIndex = Blocks.ContainsKey(block.MediaType) ? Blocks[block.MediaType].IndexOf(clockPosition) : 0;
            this.LogRenderBlock(block, clockPosition, blockIndex);
            return 1;
        }

        /// <summary>
        /// Tries to receive the next frame from the decoder by decoding queued
        /// Packets and converting the decoded frame into a Media Block which gets
        /// enqueued into the playback block buffer.
        /// </summary>
        /// <param name="t">The MediaType.</param>
        /// <returns>True if a block could be added. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AddNextBlock(MediaType t)
        {
            // Decode the frames
            var block = Blocks[t].Add(Container.Components[t].ReceiveNextFrame(), Container);
            if (block != null) return true;

            return false;
        }

        #endregion
    }
}
