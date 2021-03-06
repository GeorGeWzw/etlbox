﻿namespace ETLBox.DataFlow
{
    /// <summary>
    /// Contains static information which affects all Dataflow tasks in ETLBox.
    /// Here you can set the threshold value when information about processed records should appear.
    /// </summary>
    public static class DataFlow
    {
        /// <summary>
        /// To avoid getting log message for every message, by default only log message are produced when 1000 rows
        /// are processed. Set this property to decrease or increase this value for a data flow components.
        /// This is the default value. Each logging threshold for rows can overwritten in a dataflow component seperately.
        /// </summary>
        public static int? LoggingThresholdRows { get; set; } = DEFAULT_LOGGING_THRESHOLD_ROW;

        /// <summary>
        /// The default value for <see cref="LoggingThresholdRows"/>
        /// </summary>
        public const int DEFAULT_LOGGING_THRESHOLD_ROW = 1000;

        /// <summary>
        /// The default maximum size for all buffers in the dataflow.
        /// This is the default value. Each maximum buffer size value can overwritten in a dataflow component seperately.
        /// </summary>
        public static int MaxBufferSize { get; set; } = DEFAULT_MAX_BUFFER_SIZE;

        /// <summary>
        /// The default value for <see cref="MaxBufferSize"/>
        /// </summary>
        public const int DEFAULT_MAX_BUFFER_SIZE = 100000;

        /// <summary>
        /// Set all settings back to default (which is null or false)
        /// </summary>
        public static void ClearSettings()
        {
            LoggingThresholdRows = DEFAULT_LOGGING_THRESHOLD_ROW;
            MaxBufferSize = DEFAULT_MAX_BUFFER_SIZE;
        }
    }
}
