using System.Globalization;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Minimal JSONL field-scanning helpers. Used by SequenceReader to parse
	/// extrinsics.jsonl — the schema is controlled
	/// by the matching writers in C++ and C# so we don't pull in a JSON library
	/// just for this. Field lookup is by exact key, value parsing is forgiving
	/// of whitespace inside arrays but not of nested objects.
	/// </summary>
	internal static class JsonlScan
	{
		public static int ReadInt(string line, string key, int fallback = 0)
		{
			if (!TryFindValueStart(line, key, out int valStart)) return fallback;
			int valEnd = ScanScalarEnd(line, valStart);
			var span = line.Substring(valStart, valEnd - valStart).Trim();
			return int.TryParse(span, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
		}

		public static long ReadLong(string line, string key, long fallback = 0)
		{
			if (!TryFindValueStart(line, key, out int valStart)) return fallback;
			int valEnd = ScanScalarEnd(line, valStart);
			var span = line.Substring(valStart, valEnd - valStart).Trim();
			return long.TryParse(span, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
		}

		public static string ReadString(string line, string key, string fallback = "")
		{
			if (!TryFindValueStart(line, key, out int valStart)) return fallback;
			if (valStart >= line.Length || line[valStart] != '"') return fallback;
			int end = line.IndexOf('"', valStart + 1);
			if (end < 0) return fallback;
			return line.Substring(valStart + 1, end - valStart - 1);
		}

		public static bool ReadVec3(string line, string key, out Vector3 vec)
		{
			if (TryReadVec3(line, key, out vec)) return true;
			vec = Vector3.zero;
			return false;
		}

		public static bool TryReadVec3(string line, string key, out Vector3 vec)
		{
			vec = Vector3.zero;
			if (!TryReadArray(line, key, out var nums) || nums.Length < 3) return false;
			vec = new Vector3(nums[0], nums[1], nums[2]);
			return true;
		}

		public static bool ReadQuat(string line, string key, out Quaternion q)
		{
			q = Quaternion.identity;
			if (!TryReadArray(line, key, out var nums) || nums.Length < 4) return false;
			q = new Quaternion(nums[0], nums[1], nums[2], nums[3]);
			return true;
		}

		private static bool TryReadArray(string line, string key, out float[] nums)
		{
			nums = null;
			if (!TryFindValueStart(line, key, out int valStart)) return false;
			if (valStart >= line.Length || line[valStart] != '[') return false;
			int end = line.IndexOf(']', valStart + 1);
			if (end < 0) return false;

			var body = line.Substring(valStart + 1, end - valStart - 1);
			var parts = body.Split(',');
			var result = new float[parts.Length];
			for (int i = 0; i < parts.Length; i++)
			{
				if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
					return false;
			}
			nums = result;
			return true;
		}

		// Locate the start of a value for a given key (skipping past ": ").
		// Returns false if the key is not in the line.
		private static bool TryFindValueStart(string line, string key, out int valueStart)
		{
			valueStart = -1;
			var needle = "\"" + key + "\"";
			int idx = line.IndexOf(needle);
			if (idx < 0) return false;
			int colon = line.IndexOf(':', idx + needle.Length);
			if (colon < 0) return false;
			int p = colon + 1;
			while (p < line.Length && line[p] == ' ') p++;
			valueStart = p;
			return p < line.Length;
		}

		// Scan until the next comma or closing brace at depth 0 (no nesting in our schema).
		private static int ScanScalarEnd(string line, int start)
		{
			int p = start;
			while (p < line.Length && line[p] != ',' && line[p] != '}') p++;
			return p;
		}
	}
}
