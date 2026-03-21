using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace GDRandomTriggerCalc;

class ChanceObject
{
    public int GroupID { get; set; }
    public float Chance { get; set; }
}


abstract class Trigger
{
    public float XPosition { get; set; }
    public abstract string Describe();
}

class RandomTrigger : Trigger
{
    public bool IsAdvanced { get; set; }
    public int GroupID1 { get; set; }
    public int GroupID2 { get; set; }
    public float ChancePercent { get; set; }
    public List<ChanceObject> ChanceObjects { get; set; } = new();

    public override string Describe()
    {
        if (IsAdvanced)
        {
            var groups = string.Join(", ", ChanceObjects.Select(c => $"Group {c.GroupID} ({c.Chance}%)"));
            return $"[Advanced Random] x={XPosition} | {groups}";
        }
        else
        {
            return $"[Basic Random]    x={XPosition} | Group {GroupID1} ({ChancePercent}%) vs Group {GroupID2} ({100 - ChancePercent}%)";
        }
    }

    // Returns the group ID with the lowest probability
    public int RarestGroupID()
    {
        if (IsAdvanced)
        {
            return ChanceObjects.OrderBy(c => c.Chance).First().GroupID;
        }
        else
        {
            return ChancePercent <= 50f ? GroupID1 : GroupID2;
        }
    }
}

// Represents a trigger that advances the seed but produces no random output we care about
class MiscTrigger : Trigger
{
    public string TriggerName { get; set; } = "";
    public int Advances { get; set; }

    public override string Describe() => $"[{TriggerName}] x={XPosition}";
}

class LevelParser
{
    public static (List<Trigger> triggers, List<string> warnings) Parse(string gmdPath)
    {
        // Read and parse XML
        var xml = File.ReadAllText(gmdPath);
        var doc = new XmlDocument();
        doc.LoadXml(xml);

        XmlNodeList nodes = (doc.SelectSingleNode("//dict")?.ChildNodes)
            ?? throw new Exception("Could not find dict node in .gmd file.");
        string? levelData = null;

        for (int i = 0; i < nodes.Count - 1; i++)
        {
            if (nodes[i]!.Name == "k" && nodes[i]!.InnerText == "k4")
            {
                levelData = nodes[i + 1]!.InnerText;
                break;
            }
        }

        if (levelData == null)
        {
            throw new Exception("Could not find level data (k4) in .gmd file.");
        }

        // Base64 decode
        levelData = levelData.Replace('-', '+').Replace('_', '/');
        levelData += new string('=', (4 - levelData.Length % 4) % 4);
        byte[] compressedLevelData = Convert.FromBase64String(levelData);

        // Gzip decompress
        string decompressedLevelData;
        using (var ms = new MemoryStream(compressedLevelData))
        using (var gs = new GZipStream(ms, CompressionMode.Decompress))
        using (var sr = new StreamReader(gs, Encoding.UTF8))
        {
            decompressedLevelData = sr.ReadToEnd();
        }

        var levelDataSections = decompressedLevelData.Split(';');
        var triggers = new List<Trigger>();
        var warnings = new List<string>();

        // Parse seed-affecting triggers
        foreach (var section in levelDataSections.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            var keyValuePairs = section.Split(',');
            var obj = new Dictionary<string, string>();
            for (int i = 0; i + 1 < keyValuePairs.Length; i += 2)
            {
                obj[keyValuePairs[i]] = keyValuePairs[i + 1];
            }

            if (!obj.TryGetValue("1", out var objectID))
            {
                continue;
            }

            float xpos = float.Parse(obj.GetValueOrDefault("2", "0"));

            switch (objectID)
            {
                case "1912": // Basic Random Trigger
                    {
                        triggers.Add(new RandomTrigger
                        {
                            IsAdvanced = false,
                            XPosition = xpos,
                            GroupID1 = int.Parse(obj.GetValueOrDefault("51", "0")),
                            GroupID2 = int.Parse(obj.GetValueOrDefault("71", "0")),
                            ChancePercent = float.Parse(obj.GetValueOrDefault("10", "50"))
                        });
                        break;
                    }

                case "2068": // Advanced Random Trigger
                    {
                        var trigger = new RandomTrigger { IsAdvanced = true, XPosition = xpos };
                        if (obj.TryGetValue("152", out var encodedChances))
                        {
                            var chanceTokens = encodedChances.Split('.');
                            for (int i = 0; i + 1 < chanceTokens.Length; i += 2)
                            {
                                trigger.ChanceObjects.Add(new ChanceObject
                                {
                                    GroupID = int.Parse(chanceTokens[i]),
                                    Chance = float.Parse(chanceTokens[i + 1])
                                });
                            }
                        }
                        triggers.Add(trigger);
                        break;
                    }

                case "1268": // Spawn Trigger
                    {
                        // Advances seed once if delay variance (key 556) is non-zero
                        float delayVariance = float.Parse(obj.GetValueOrDefault("556", "0"));
                        if (delayVariance != 0)
                        {
                            triggers.Add(new MiscTrigger
                            {
                                XPosition = xpos,
                                TriggerName = "Spawn Trigger (delay variance)",
                                Advances = 1
                            });
                        }
                        break;
                    }

                case "3016": // Advanced Follow Trigger
                    {
                        // Advances seed on first activation per object:
                        // - Once if start speed variance is non-zero
                        // - Twice if start direction variance is also non-zero AND start speed is non-zero
                        // Note: actual count depends on number of objects in target group — this is approximate
                        float startSpeedVariance = float.Parse(obj.GetValueOrDefault("301", "0"));
                        float startSpeed = float.Parse(obj.GetValueOrDefault("300", "0"));
                        float startDirVariance = float.Parse(obj.GetValueOrDefault("564", "0"));

                        if (startSpeedVariance != 0)
                        {
                            int advances = 1;
                            if (startDirVariance != 0 && startSpeed != 0)
                            {
                                advances = 2;
                            }

                            warnings.Add($"Advanced Follow Trigger at x={xpos}: seed advance count is approximate ({advances}). " +
                                         "Actual count may vary if the target group contains multiple objects.");
                            triggers.Add(new MiscTrigger
                            {
                                XPosition = xpos,
                                TriggerName = "Advanced Follow Trigger",
                                Advances = advances
                            });
                        }
                        break;
                    }
            }
        }

        triggers.Sort((a, b) => a.XPosition.CompareTo(b.XPosition));
        return (triggers, warnings);
    }
}

class RNG
{
    private const ulong Multiplier = 214013;
    private const ulong Increment = 2531011;

    // GD uses a 64-bit LCG (IMUL RAX, qword ptr confirmed in assembly)
    public static ulong Advance(ulong seed) =>
        seed * Multiplier + Increment;

    // Basic trigger: raw float, no rounding (direct comparison in GD source)
    public static float RollBasic(ulong seed) =>
        ((seed >> 16) & 0x7FFFUL) / 32767.0f * 100.0f;

    // Advanced trigger: rounded to nearest integer
    public static int RollAdvanced(ulong seed, float totalChance) =>
        (int)MathF.Round(((seed >> 16) & 0x7FFFUL) / 32767.0f * totalChance);
}

class Simulator
{
    public static (int groupID, ulong newSeed) ActivateTrigger(RandomTrigger randomTrigger, ulong seed)
    {
        ulong newSeed = RNG.Advance(seed);

        if (randomTrigger.IsAdvanced)
        {
            float totalChance = randomTrigger.ChanceObjects.Sum(c => c.Chance);
            float roll = RNG.RollAdvanced(newSeed, totalChance);
            float accumulator = 0;
            foreach (var co in randomTrigger.ChanceObjects)
            {
                accumulator += co.Chance;
                if (roll <= accumulator)
                {
                    return (co.GroupID, newSeed);
                }
            }
            return (0, newSeed);
        }
        else
        {
            // Basic trigger: group1 if roll <= chancePercent, group2 if roll > chancePercent
            float roll = RNG.RollBasic(newSeed);
            int groupID = roll <= randomTrigger.ChancePercent ? randomTrigger.GroupID1 : randomTrigger.GroupID2;
            return (groupID, newSeed);
        }
    }

    public static List<(string label, int? groupID, ulong seedBefore, ulong seedAfter)>
        PredictAll(List<Trigger> triggers, ulong seedInput)
    {
        var results = new List<(string, int?, ulong, ulong)>();
        ulong seed = seedInput;

        // Level start advance
        ulong levelStartSeed = RNG.Advance(seed);
        results.Add(("[Level Start]", null, seed, levelStartSeed));
        seed = levelStartSeed;

        foreach (var trigger in triggers)
        {
            ulong seedBefore = seed;

            if (trigger is RandomTrigger randomTrigger)
            {
                var (groupID, newSeed) = ActivateTrigger(randomTrigger, seed);
                results.Add((trigger.Describe(), groupID, seedBefore, newSeed));
                seed = newSeed;
            }
            else if (trigger is MiscTrigger miscTrigger)
            {
                for (int i = 0; i < miscTrigger.Advances; i++)
                {
                    seed = RNG.Advance(seed);
                }
                results.Add((trigger.Describe(), null, seedBefore, seed));
            }
        }

        return results;
    }

    // Searches all seeds, streaming results to console as found (max 20)
    // Returns when 20 seeds found, ulong.MaxValue reached, or escape pressed
    public static void SearchSeeds(
        List<Trigger> triggers,
        RandomTrigger targetTrigger,
        int desiredGroupID,
        int limit = 20)
    {
        int validSeeds = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (ulong seedIndex = 0; ; seedIndex++)
        {
            // Check for escape key
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                Console.WriteLine($"\nCancelled after {stopwatch.Elapsed.TotalSeconds:F1}s.");
                break;
            }

            // Advance once for level start
            ulong seed = RNG.Advance(seedIndex);

            foreach (var trigger in triggers)
            {
                if (trigger is RandomTrigger randomTrigger)
                {
                    var (groupID, newSeed) = ActivateTrigger(randomTrigger, seed);
                    seed = newSeed;
                    if (ReferenceEquals(randomTrigger, targetTrigger))
                    {
                        if (groupID == desiredGroupID)
                        {
                            validSeeds++;
                            Console.WriteLine($"  [{validSeeds}] Seed: {seedIndex} (elapsed: {stopwatch.Elapsed.TotalSeconds:F1}s)");
                        }
                        break;
                    }
                }
                else if (trigger is MiscTrigger advancer)
                {
                    for (int i = 0; i < advancer.Advances; i++)
                    {
                        seed = RNG.Advance(seed);
                    }
                }
            }

            if (validSeeds >= limit || seedIndex == ulong.MaxValue)
            {
                Console.WriteLine($"\nDone. Found {validSeeds} seed(s) in {stopwatch.Elapsed.TotalSeconds:F1}s.");
                break;
            }
        }
    }

    // Searches all seeds, streaming results to console as found (max 20)
    // Returns when 20 seeds found, ulong.MaxValue reached, or escape pressed
    public static void SearchRarestSeeds(List<Trigger> triggers, List<RandomTrigger> randomTriggers)
    {
        int limit = 20;
        var rarestGroups = randomTriggers.ToDictionary(rt => rt, rt => rt.RarestGroupID());
        int found = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (ulong seedIndex = 0; ; seedIndex++)
        {
            // Check for escape key
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape)
            {
                Console.WriteLine($"\nCancelled after {stopwatch.Elapsed.TotalSeconds:F1}s.");
                break;
            }

            // Advance once for level start
            ulong seed = RNG.Advance(seedIndex);
            bool allMatch = true;

            foreach (var trigger in triggers)
            {
                if (trigger is RandomTrigger randomTrigger)
                {
                    var (groupID, newSeed) = ActivateTrigger(randomTrigger, seed);
                    seed = newSeed;
                    if (rarestGroups[randomTrigger] != groupID)
                    {
                        allMatch = false;
                        break;
                    }
                }
                else if (trigger is MiscTrigger advancer)
                {
                    for (int i = 0; i < advancer.Advances; i++)
                    {
                        seed = RNG.Advance(seed);
                    }
                }
            }

            if (allMatch)
            {
                found++;
                Console.WriteLine($"  [{found}] Seed: {seedIndex} (elapsed: {stopwatch.Elapsed.TotalSeconds:F1}s)");
            }

            if (found >= limit || seedIndex == ulong.MaxValue)
            {
                Console.WriteLine($"\nDone. Found {found} seed(s) in {stopwatch.Elapsed.TotalSeconds:F1}s.");
                break;
            }
        }
    }
}

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.WriteLine("=== GD Random Trigger Seed Calculator ===");
        Console.WriteLine("Note: This tool assumes all triggers are activated in left-to-right order based on X position.");
        Console.WriteLine("      It does not account for the following cases which may cause inaccurate results:");
        Console.WriteLine("      - Rotate triggers with aim mode or direction follow mode targeting groups with multiple objects");
        Console.WriteLine("      - Advanced Follow triggers targeting groups with multiple objects");
        Console.WriteLine("      - Any other mechanics that may affect trigger activation order");
        Console.WriteLine();
        Console.WriteLine("Press Enter to upload your .gmd file.");
        Console.WriteLine();
        while (Console.ReadKey(true).Key != ConsoleKey.Enter)
        {
            ;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        string gmdPath;
        using (var dialog = new OpenFileDialog
        {
            Title = "Select .gmd file",
            Filter = "GD Level Files (*.gmd)|*.gmd"
        })
        {
            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            gmdPath = dialog.FileName;
        }

        if (Path.GetExtension(gmdPath).ToLower() != ".gmd")
        {
            Console.WriteLine("Please select a .gmd file.");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey(true);
            return;
        }

        List<Trigger> triggers;
        List<string> warnings;
        try
        {
            (triggers, warnings) = LevelParser.Parse(gmdPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse level: {ex.Message}");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey(true);
            return;
        }

        var randomTriggers = triggers.OfType<RandomTrigger>().ToList();
        var miscTriggers = triggers.OfType<MiscTrigger>().ToList();

        if (randomTriggers.Count == 0)
        {
            Console.WriteLine("No random triggers found in level.");
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey(true);
            return;
        }

        if (warnings.Count > 0)
        {
            Console.WriteLine("Warnings:");
            foreach (var w in warnings)
            {
                Console.WriteLine($"  ! {w}");
            }
            Console.WriteLine();
        }

        if (miscTriggers.Count > 0)
        {
            Console.WriteLine($"Found {miscTriggers.Count} other seed-affecting trigger(s):");
            foreach (var mt in miscTriggers)
            {
                Console.WriteLine($"  {mt.Describe()} ({mt.Advances} advance(s))");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Found {randomTriggers.Count} random trigger(s):");
        for (int i = 0; i < randomTriggers.Count; i++)
        {
            Console.WriteLine($"  [{i}] {randomTriggers[i].Describe()}");
        }

        Console.WriteLine();
        Console.WriteLine("Mode:");
        Console.WriteLine("  1. Predict outcomes from a starting seed");
        Console.WriteLine("  2. Find first 20 seeds that produce a desired outcome for one random trigger");
        Console.WriteLine("  3. Find first 20 seeds that produce the rarest outcome for all random triggers");
        Console.Write("Choice: ");
        var choice = Console.ReadLine()?.Trim();

        if (choice == "1")
        {
            Console.Write("Enter seed input: ");
            if (!ulong.TryParse(Console.ReadLine(), out ulong seedInput))
            {
                Console.WriteLine("Invalid seed.");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey(true);
                return;
            }

            var results = Simulator.PredictAll(triggers, seedInput);
            Console.WriteLine();
            Console.WriteLine("Results:");
            foreach (var (label, groupID, seedBefore, seedAfter) in results)
            {
                Console.WriteLine($"  {label}");
                if (groupID.HasValue)
                {
                    Console.WriteLine($"    Seed {seedBefore} -> {seedAfter} -> Group {groupID}");
                }
                else
                {
                    Console.WriteLine($"    Seed {seedBefore} -> {seedAfter}");
                }
            }
        }
        else if (choice == "2")
        {
            Console.Write($"Enter trigger index (0-{randomTriggers.Count - 1}): ");
            if (!int.TryParse(Console.ReadLine(), out int triggerIndex)
                || triggerIndex < 0
                || triggerIndex >= randomTriggers.Count)
            {
                Console.WriteLine("Invalid index.");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey(true);
                return;
            }

            var targetTrigger = randomTriggers[triggerIndex];
            HashSet<int> validGroups = targetTrigger.IsAdvanced
                ? targetTrigger.ChanceObjects.Select(c => c.GroupID).ToHashSet()
                : new HashSet<int> { targetTrigger.GroupID1, targetTrigger.GroupID2 };

            Console.WriteLine($"Valid groups: {string.Join(", ", validGroups)}");
            Console.Write("Enter desired group ID: ");
            if (!int.TryParse(Console.ReadLine(), out int desiredGroup))
            {
                Console.WriteLine("Invalid group ID.");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey(true);
                return;
            }

            if (!validGroups.Contains(desiredGroup))
            {
                Console.WriteLine($"Invalid group ID. Valid groups: {string.Join(", ", validGroups)}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey(true);
                return;
            }

            Console.WriteLine("Searching... (press Escape to cancel)");
            Simulator.SearchSeeds(triggers, targetTrigger, desiredGroup);
        }
        else if (choice == "3")
        {
            Console.WriteLine("Rarest outcome per trigger:");
            foreach (var rt in randomTriggers)
            {
                Console.WriteLine($"  {rt.Describe()} -> rarest group: {rt.RarestGroupID()}");
            }
            Console.WriteLine();
            Console.WriteLine("Searching... (press Escape to cancel)");
            Simulator.SearchRarestSeeds(triggers, randomTriggers);
        }
        else
        {
            Console.WriteLine("Invalid choice.");
        }

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey(true);
    }
}