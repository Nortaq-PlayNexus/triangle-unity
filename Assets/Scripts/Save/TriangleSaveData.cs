using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triangle.Save
{
    /// <summary>
    /// Serializable dream-state snapshot for a save slot. The current vertical slice
    /// persists player progression, pattern memory, notebook progress, environment
    /// identity, wake depth, and owned world flags.
    ///
    /// Port of UTriangleSaveGame. JsonUtility cannot serialise dictionaries, so the
    /// environment flag map became a parallel key/value list pair.
    /// </summary>
    [Serializable]
    public class TriangleSaveData
    {
        /// <summary>Version written by the current persistence format. Zero denotes a pre-versioned save.</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// Marker written into every save this build produces.
        ///
        /// Schema version alone cannot tell a save from a file that merely parses. A
        /// truncated or hand-edited JSON document, or an unrelated file dropped at the
        /// slot path, will happily deserialise into a default-constructed object - all
        /// zeroes, empty lists, schema 0 - and every check downstream passes. The player
        /// then loads what looks like a real save and silently loses all progression.
        /// A literal marker turns that class of mistake into a refused load.
        ///
        /// Pre-signature saves (schema 1 and earlier) legitimately have an empty value
        /// here, so an absent marker is tolerated for them and only required from
        /// <see cref="CurrentSchemaVersion"/> onward.
        /// </summary>
        public const string ExpectedSignature = "THE_TRIANGLE_DREAM";

        /// <summary>Format marker. Empty on pre-signature saves.</summary>
        public string Signature = string.Empty;

        /// <summary>Version of the serialized field layout used by this snapshot.</summary>
        public int SchemaVersion;

        /// <summary>Absolute player position retained for legacy/debug compatibility.</summary>
        public Vector3 PlayerPosition = Vector3.zero;

        public Vector3 PlayerEulerAngles = Vector3.zero;

        /// <summary>Player transform relative to the active environment's generated start.</summary>
        public Vector3 PlayerPositionInEnvironment = Vector3.zero;

        /// <summary>Player facing relative to the active environment's generated start.</summary>
        public Vector3 PlayerForwardInEnvironment = Vector3.forward;

        /// <summary>True when PlayerPositionInEnvironment is authoritative for schema 2+.</summary>
        public bool HasPlayerTransformInEnvironment;

        /// <summary>Current hidden Lucidity value (0..100).</summary>
        public float Lucidity;

        /// <summary>Current hidden Recognition value (0..100).</summary>
        public float Recognition;

        /// <summary>Stable pattern IDs already recorded in the player's Recognition catalogue.</summary>
        public List<string> RecognizedPatternIds = new List<string>();

        /// <summary>Legacy compatibility flag for pre-count notebook saves.</summary>
        public bool NotebookInteracted;

        /// <summary>Total notebook reads, including repeated reads of earlier pages.</summary>
        public int NotebookInteractionCount;

        /// <summary>Number of distinct Triangle patterns currently catalogued.</summary>
        public int TriangleEncounters;

        /// <summary>Dream fragments discovered (symbols, phrases, locations).</summary>
        public List<string> DiscoveredMemories = new List<string>();

        /// <summary>Stable IDs for notebook pages added so far.</summary>
        public List<string> NotebookPages = new List<string>();

        /// <summary>Stable identifier for the environment containing the player.</summary>
        public string CurrentEnvironmentId = string.Empty;

        /// <summary>Number of completed false awakenings; zero means still in the first dream layer.</summary>
        public int FalseAwakeningLayer;

        /// <summary>Whether the childhood bedroom's interactive door is open.</summary>
        public bool BedroomDoorOpen;

        /// <summary>Environment flags (lights off and other extensible world state).</summary>
        public List<string> EnvironmentFlagKeys = new List<string>();
        public List<bool> EnvironmentFlagValues = new List<bool>();

        public bool IsFinite()
        {
            return IsFinite(PlayerPosition) && IsFinite(PlayerEulerAngles)
                   && IsFinite(PlayerPositionInEnvironment) && IsFinite(PlayerForwardInEnvironment)
                   && !float.IsNaN(Lucidity) && !float.IsInfinity(Lucidity)
                   && !float.IsNaN(Recognition) && !float.IsInfinity(Recognition);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                   && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                   && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        public bool GetEnvironmentFlag(string key, bool fallback)
        {
            if (EnvironmentFlagKeys == null)
            {
                return fallback;
            }

            for (int i = 0; i < EnvironmentFlagKeys.Count && i < EnvironmentFlagValues.Count; i++)
            {
                if (EnvironmentFlagKeys[i] == key)
                {
                    return EnvironmentFlagValues[i];
                }
            }

            return fallback;
        }

        public void SetEnvironmentFlag(string key, bool value)
        {
            EnvironmentFlagKeys ??= new List<string>();
            EnvironmentFlagValues ??= new List<bool>();
            for (int i = 0; i < EnvironmentFlagKeys.Count; i++)
            {
                if (EnvironmentFlagKeys[i] == key)
                {
                    EnvironmentFlagValues[i] = value;
                    return;
                }
            }

            EnvironmentFlagKeys.Add(key);
            EnvironmentFlagValues.Add(value);
        }
    }
}
