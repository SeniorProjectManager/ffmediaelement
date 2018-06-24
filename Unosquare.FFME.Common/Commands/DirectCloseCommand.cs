﻿namespace Unosquare.FFME.Commands
{
    using Core;
    using Shared;
    using System;
    using System.Text;

    /// <summary>
    /// Close Command Implementation
    /// </summary>
    /// <seealso cref="DirectMediaCommand" />
    internal sealed class DirectCloseCommand : DirectMediaCommand
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DirectCloseCommand"/> class.
        /// </summary>
        /// <param name="mediaCore">The media core.</param>
        public DirectCloseCommand(MediaEngine mediaCore)
            : base(mediaCore)
        {
            CommandType = MediaCommandType.Close;
        }

        /// <summary>
        /// Gets the command type identifier.
        /// </summary>
        public override MediaCommandType CommandType { get; }

        /// <summary>
        /// Performs actions when the command has been executed.
        /// This is useful to notify exceptions or update the state of the media.
        /// </summary>
        public override void PostProcess()
        {
            MediaCore.SendOnMediaClosed();
            LogReferenceCounter(MediaCore);
            MediaCore.Log(MediaLogMessageType.Debug, $"Command {CommandType}: Completed");
        }

        /// <summary>
        /// Performs the actions represented by this deferred task.
        /// </summary>
        protected override void PerformActions()
        {
            var m = MediaCore;
            m.Log(MediaLogMessageType.Debug, $"Command {CommandType}: Entered");
            m.StopWorkers();

            // Dispose the container
            if (m.Container != null)
            {
                m.Container.Dispose();
                m.Container = null;
            }

            // Dispose the Blocks for all components
            foreach (var kvp in m.Blocks)
                kvp.Value.Dispose();

            m.Blocks.Clear();
            m.DisposePreloadedSubtitles();

            // Clear the render times
            m.LastRenderTime.Clear();

            // Update notification properties
            m.State.ResetMediaProperties();
            m.State.InitializeBufferingProperties();
            m.State.UpdateMediaState(PlaybackStatus.Close, TimeSpan.Zero);
            m.State.Source = null;
        }

        /// <summary>
        /// Outputs Reference Counter Results
        /// </summary>
        /// <param name="m">The underlying media engine.</param>
        private static void LogReferenceCounter(MediaEngine m)
        {
            if (MediaEngine.Platform.IsInDebugMode) return;
            if (RC.Current.InstancesByLocation.Count <= 0) return;

            var builder = new StringBuilder();
            builder.AppendLine("Unmanaged references were left alive. This is an indication that there is a memory leak.");
            foreach (var kvp in RC.Current.InstancesByLocation)
                builder.AppendLine($"    {kvp.Key,30}: {kvp.Value}");

            m.Log(MediaLogMessageType.Error, builder.ToString());
        }
    }
}
