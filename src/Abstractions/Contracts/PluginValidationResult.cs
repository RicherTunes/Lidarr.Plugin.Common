using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Lightweight validation result used when exchanging settings or capability checks across the host boundary.
    /// </summary>
    public sealed class PluginValidationResult
    {
        private PluginValidationResult(bool isValid, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
        {
            IsValid = isValid;
            Errors = errors;
            Warnings = warnings;
        }

        public bool IsValid { get; }

        public IReadOnlyList<string> Errors { get; }

        public IReadOnlyList<string> Warnings { get; }

        public static PluginValidationResult Success(params string[] warnings)
        {
            return new PluginValidationResult(true, Array.Empty<string>(), ToReadOnly(warnings));
        }

        public static PluginValidationResult Failure(IEnumerable<string> errors, IEnumerable<string>? warnings = null)
        {
            if (errors is null) throw new ArgumentNullException(nameof(errors));
            var errorList = errors.Where(e => !string.IsNullOrWhiteSpace(e)).ToArray();
            if (errorList.Length == 0)
            {
                errorList = new[] { "Unknown validation failure." };
            }

            return new PluginValidationResult(false, ToReadOnly(errorList), ToReadOnly(warnings));
        }

        private static IReadOnlyList<string> ToReadOnly(IEnumerable<string>? values)
        {
            if (values is null) return Array.Empty<string>();
            var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            return new ReadOnlyCollection<string>(list);
        }
    }
}
