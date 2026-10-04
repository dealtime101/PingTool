using System.Text.Json;

namespace PingTool
{
    // Sharing profiles as a file: "Export profiles" writes every saved profile, "Import profiles" reads
    // one back (yours from another PC, or a colleague's). The file is untrusted input: it is parsed
    // strictly, every number is pulled into range, targets that the address box would refuse are
    // dropped, and nothing replaces a profile of yours without the user being told which ones.
    internal static class ProfileExchange
    {
        public const string FormatName = "pingtool-profiles";
        public const int FormatVersion = 1;
        public const int MaxFileBytes = 1_000_000;
        public const int MaxHostsPerProfile = 100;

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
        // A file edited by hand may say "name" or "hosts": the case of a key is not worth a refusal.
        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new LenientInt() },
        };

        // A number in a file written by hand: "5" between quotes is 5, and 1e12 does not fit an int: it is pulled to the nearest
        // int and ProfileBook.Sanitize brings it into the range of its setting (the promise of the comment above), instead of the
        // JSON reader refusing the whole file. Anything that is not a number at all still fails, for that profile only.
        private sealed class LenientInt : System.Text.Json.Serialization.JsonConverter<int>
        {
            public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                double value;
                if (reader.TokenType == JsonTokenType.Number) value = reader.GetDouble();
                else if (reader.TokenType == JsonTokenType.String
                         && double.TryParse(reader.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) { }
                else throw new JsonException($"The JSON value could not be converted to a number.");
                if (double.IsNaN(value)) throw new JsonException("The JSON value is not a number.");
                return (int)Math.Clamp(Math.Truncate(value), int.MinValue, int.MaxValue);
            }

            public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
        }

        private sealed class Document
        {
            public string Format { get; set; } = FormatName;
            public int Version { get; set; } = FormatVersion;
            public List<Profile> Profiles { get; set; } = new();
        }

        // What was usable in a file, and what was left out (to say so rather than lose it silently).
        internal sealed record Imported(List<Profile> Profiles, int ProfilesDropped, int TargetsDropped);

        public static string Export(IEnumerable<Profile> profiles) =>
            JsonSerializer.Serialize(new Document { Profiles = profiles.ToList() }, WriteOptions);

        // A file made by Export, or just a JSON list of profiles.
        public static bool TryParse(string json, out Imported imported, out string error)
        {
            imported = new Imported(new List<Profile>(), 0, 0);
            error = "";

            // Bytes, as the name says: a character takes up to 4. (MainForm already refuses a bigger FILE before reading it; this
            // protects every other way to hand a string in.) Length first: it is free, and a string longer than the limit in
            // characters is longer in bytes.
            if (json.Length > MaxFileBytes || System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            {
                error = "That file is too large to be a list of profiles.";
                return false;
            }

            List<Profile?>? raw;
            string where = "$";   // where the list sits in the file, for an error found inside it
            string? firstProblem = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement list;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    list = root;
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    if (Find(root, "Format") is { ValueKind: JsonValueKind.String } f && f.GetString() != FormatName)
                    {
                        error = "This file is not a PingTool profiles file.";
                        return false;
                    }

                    if (Find(root, "Version") is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out int version) && version > FormatVersion)
                    {
                        error = "This file was made by a newer PingTool: update PingTool to read it.";
                        return false;
                    }

                    if (Find(root, "Profiles") is not { ValueKind: JsonValueKind.Array } p)
                    {
                        error = "This file does not contain a list of profiles.";
                        return false;
                    }

                    list = p;
                    where = "$.Profiles";
                }
                else
                {
                    error = "This file does not contain a list of profiles.";
                    return false;
                }

                // One profile at a time: a profile that cannot be read is dropped (and counted), the others are kept. Each element is
                // read as itself, not as a copy of its text, so an error names a place in the FILE (its path), not in a copy.
                raw = new List<Profile?>();
                int index = 0;
                foreach (var element in list.EnumerateArray())
                {
                    try
                    {
                        raw.Add(element.Deserialize<Profile?>(ReadOptions));
                    }
                    catch (JsonException ex)
                    {
                        raw.Add(null);
                        string at = where + "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]" + (ex.Path is { Length: > 1 } path ? path[1..] : "");
                        int cut = ex.Message.IndexOf(" Path:", StringComparison.Ordinal);
                        firstProblem ??= $"A profile in this file is not valid: {(cut > 0 ? ex.Message[..cut] : ex.Message)} (at {at}).";
                    }

                    index++;
                }
            }
            catch (JsonException ex)
            {
                // The file itself is not JSON: here the line and position are those of the file.
                error = "This file is not valid JSON: " + ex.Message;
                return false;
            }

            int targetsDropped = 0;
            var usable = new List<Profile?>();
            foreach (var profile in raw ?? new List<Profile?>())
            {
                if (profile is null) { usable.Add(null); continue; }

                var hosts = new List<string>();
                foreach (string? host in profile.Hosts ?? new List<string>())
                {
                    string text = (host ?? "").Trim();
                    bool fine = ProbeTarget.TryParse(text, out _, out _)
                        && !hosts.Contains(text, StringComparer.OrdinalIgnoreCase)
                        && hosts.Count < MaxHostsPerProfile;
                    if (fine) hosts.Add(text); else targetsDropped++;
                }

                profile.Hosts = hosts;
                usable.Add(hosts.Count > 0 ? profile : null);   // a profile with nothing to monitor is no profile
            }

            // Names, numbers in range, duplicates and the 20-profile limit are ProfileBook's job.
            var clean = ProfileBook.Sanitize(usable);
            imported = new Imported(clean, (raw?.Count ?? 0) - clean.Count, targetsDropped);
            if (clean.Count == 0)
            {
                error = firstProblem ?? "No usable profile in this file.";
                return false;
            }

            return true;
        }

        // The profiles of the book that importing would replace (same name, any case).
        public static List<string> Replaced(IEnumerable<Profile> book, IEnumerable<Profile> incoming) =>
            incoming.Where(i => ProfileBook.Find(book, i.Name) is not null).Select(i => i.Name).ToList();

        // Puts the imported profiles in the book, within its limit. Returns how many did not fit.
        public static int Merge(List<Profile> book, IEnumerable<Profile> incoming)
        {
            int notFitting = 0;
            foreach (var profile in incoming)
                if (!ProfileBook.Upsert(book, profile)) notFitting++;
            return notFitting;
        }

        private static JsonElement? Find(JsonElement obj, string name)
        {
            foreach (var property in obj.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
            return null;
        }
    }
}
