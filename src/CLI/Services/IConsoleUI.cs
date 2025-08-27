using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.CLI.UI
{
    /// <summary>
    /// Interface for console user interface operations
    /// Abstracts console interaction for testing and different UI implementations
    /// </summary>
    public interface IConsoleUI
    {
        /// <summary>
        /// Write markup text with formatting
        /// </summary>
        void WriteMarkup(string markup);

        /// <summary>
        /// Write a line of markup text
        /// </summary>
        void WriteMarkupLine(string markup);

        /// <summary>
        /// Write plain text
        /// </summary>
        void Write(string text);

        /// <summary>
        /// Write a line of plain text
        /// </summary>
        void WriteLine(string text = "");

        /// <summary>
        /// Write an error message
        /// </summary>
        void WriteError(string message);

        /// <summary>
        /// Write a warning message
        /// </summary>
        void WriteWarning(string message);

        /// <summary>
        /// Write a success message
        /// </summary>
        void WriteSuccess(string message);

        /// <summary>
        /// Ask user for input
        /// </summary>
        string Ask(string prompt);

        /// <summary>
        /// Ask user for password (hidden input)
        /// </summary>
        string AskPassword(string prompt);

        /// <summary>
        /// Ask user for confirmation
        /// </summary>
        bool Confirm(string prompt, bool defaultValue = false);

        /// <summary>
        /// Show selection prompt
        /// </summary>
        T Select<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector = null);

        /// <summary>
        /// Show multi-selection prompt
        /// </summary>
        IEnumerable<T> MultiSelect<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector = null);

        /// <summary>
        /// Display a table of data
        /// </summary>
        void ShowTable<T>(IEnumerable<T> data, params (string header, Func<T, string> getValue)[] columns);

        /// <summary>
        /// Show progress for a long-running operation
        /// </summary>
        Task<T> ShowProgressAsync<T>(string taskDescription, Func<IProgress<ProgressInfo>, Task<T>> operation);

        /// <summary>
        /// Show status information
        /// </summary>
        void ShowStatus(string title, Dictionary<string, object> data);

        /// <summary>
        /// Clear the console
        /// </summary>
        void Clear();
    }

    /// <summary>
    /// Progress information for UI operations
    /// </summary>
    public class ProgressInfo
    {
        public double Percentage { get; set; }
        public string CurrentTask { get; set; }
        public string Details { get; set; }
    }
}