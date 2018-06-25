﻿namespace Unosquare.FFME.Commands
{
    using Primitives;

    /// <summary>
    /// Represents a promise-style command executed in a queue.
    /// </summary>
    /// <seealso cref="PromiseBase" />
    internal abstract class CommandBase : PromiseBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandBase"/> class.
        /// </summary>
        /// <param name="mediaCore">The media core.</param>
        protected CommandBase(MediaEngine mediaCore)
            : base(continueOnCapturedContext: true)
        {
            MediaCore = mediaCore;
        }

        /// <summary>
        /// Contins a reference to the media engine associated with this command
        /// </summary>
        public MediaEngine MediaCore { get; }

        /// <summary>
        /// Gets the command type identifier.
        /// </summary>
        public abstract CommandType CommandType { get; }

        /// <summary>
        /// Gets the command category.
        /// </summary>
        public abstract CommandCategory Category { get; }
    }
}
