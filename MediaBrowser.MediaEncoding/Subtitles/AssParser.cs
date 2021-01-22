#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    public class AssParser : ISubtitleParser
    {
        private readonly CultureInfo _usCulture = new CultureInfo("en-US");

        /// <inheritdoc />
        public SubtitleTrackInfo Parse(Stream stream, CancellationToken cancellationToken)
        {
            var trackInfo = new SubtitleTrackInfo();
            var trackEvents = new List<SubtitleTrackEvent>();
            var eventIndex = 1;
            using (var reader = new StreamReader(stream))
            {
                string line;
                while (!string.Equals(reader.ReadLine(), "[Events]", StringComparison.Ordinal))
                {
                }

                var headers = ParseFieldHeaders(reader.ReadLine());

                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (line[0] == '[')
                    {
                        break;
                    }

                    var subEvent = new SubtitleTrackEvent { Id = eventIndex.ToString(_usCulture) };
                    eventIndex++;
                    const string Dialogue = "Dialogue: ";
                    var sections = line.Substring(Dialogue.Length).Split(',');

                    subEvent.StartPositionTicks = GetTicks(sections[headers["Start"]]);
                    subEvent.EndPositionTicks = GetTicks(sections[headers["End"]]);

                    subEvent.Text = string.Join(',', sections[headers["Text"]..]);
                    RemoteNativeFormatting(subEvent);

                    subEvent.Text = subEvent.Text.Replace("\\n", ParserValues.NewLine, StringComparison.OrdinalIgnoreCase);

                    subEvent.Text = Regex.Replace(subEvent.Text, @"\{(\\[\w]+\(?([\w\d]+,?)+\)?)+\}", string.Empty, RegexOptions.IgnoreCase);

                    trackEvents.Add(subEvent);
                }
            }

            trackInfo.TrackEvents = trackEvents;
            return trackInfo;
        }

        private long GetTicks(ReadOnlySpan<char> time)
        {
            return TimeSpan.TryParseExact(time, @"h\:mm\:ss\.ff", _usCulture, out var span)
                ? span.Ticks : 0;
        }

        internal static Dictionary<string, int> ParseFieldHeaders(string line)
        {
            const string Format = "Format: ";
            var fields = line.Substring(Format.Length).Split(',').Select(x => x.Trim()).ToList();

            return new Dictionary<string, int>
            {
                { "Start", fields.IndexOf("Start") },
                { "End", fields.IndexOf("End") },
                { "Text", fields.IndexOf("Text") }
            };
        }

        private void RemoteNativeFormatting(SubtitleTrackEvent p)
        {
            int indexOfBegin = p.Text.IndexOf('{', StringComparison.Ordinal);
            string pre = string.Empty;
            while (indexOfBegin >= 0 && p.Text.IndexOf('}', StringComparison.Ordinal) > indexOfBegin)
            {
                string s = p.Text.Substring(indexOfBegin);
                if (s.StartsWith("{\\an1}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an2}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an3}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an4}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an5}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an6}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an7}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an8}", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an9}", StringComparison.Ordinal))
                {
                    pre = s.Substring(0, 6);
                }
                else if (s.StartsWith("{\\an1\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an2\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an3\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an4\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an5\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an6\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an7\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an8\\", StringComparison.Ordinal) ||
                    s.StartsWith("{\\an9\\", StringComparison.Ordinal))
                {
                    pre = s.Substring(0, 5) + "}";
                }

                int indexOfEnd = p.Text.IndexOf('}', StringComparison.Ordinal);
                p.Text = p.Text.Remove(indexOfBegin, (indexOfEnd - indexOfBegin) + 1);

                indexOfBegin = p.Text.IndexOf('{', StringComparison.Ordinal);
            }

            p.Text = pre + p.Text;
        }
    }
}
