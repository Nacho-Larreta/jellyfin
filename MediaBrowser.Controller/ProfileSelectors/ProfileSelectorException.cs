using System;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents a business error specific to the profile selector flow.
    /// </summary>
    public class ProfileSelectorException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorException"/> class.
        /// </summary>
        /// <param name="statusCode">The HTTP status code that best represents the failure.</param>
        /// <param name="errorCode">The machine-readable error code.</param>
        /// <param name="message">The human-readable message.</param>
        public ProfileSelectorException(int statusCode, string errorCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }

        /// <summary>
        /// Gets the status code that should be returned by the API.
        /// </summary>
        public int StatusCode { get; }

        /// <summary>
        /// Gets the machine-readable error code.
        /// </summary>
        public string ErrorCode { get; }
    }
}
