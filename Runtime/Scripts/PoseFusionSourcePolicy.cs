using System.IO;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Decides which camera sources can carry pose fusion. Fusion needs a camera pose that
	/// something other than this tracker measured, so it is valid only with an AR camera source, which feeds
	/// frames together with the AR camera pose, and with sequence replay that recorded those camera poses
	/// (extrinsics). Every other source has none, so fusion is discarded for it.
	/// </summary>
	internal static class PoseFusionSourcePolicy
	{
		internal const string RECORDED_EXTRINSICS_FILE = "extrinsics.jsonl";

		internal enum Verdict
		{
			/// <summary>The source provides a camera pose, so the serialized choice stands.</summary>
			Allowed,

			/// <summary>
			/// A sequence is selected but its folder is not known yet (the replay names it when it starts).
			/// The serialized choice stands until it is known, so a replay that starts later is not cut short.
			/// </summary>
			SequenceNotYetKnown,

			/// <summary>The source is a live camera with no AR camera pose.</summary>
			NoCameraPose,

			/// <summary>The sequence folder holds no recorded camera pose marked valid.</summary>
			SequenceWithoutExtrinsics
		}

		/// <summary>
		/// Evaluates a camera source. The allow-list is the AR camera source (<see cref="ImageSource.Injected"/>)
		/// and a sequence whose folder holds recorded extrinsics. The sequence folder is only read for a sequence.
		/// </summary>
		internal static Verdict Evaluate(ImageSource source, string sequenceDirectory)
		{
			switch (source)
			{
				case ImageSource.Injected:
					return Verdict.Allowed;
				case ImageSource.Sequence:
					string resolved = ResolveSequenceDirectory(sequenceDirectory);
					if (string.IsNullOrEmpty(resolved) || !Directory.Exists(resolved))
						return Verdict.SequenceNotYetKnown;
					return HasRecordedCameraPose(Path.Combine(resolved, RECORDED_EXTRINSICS_FILE))
						? Verdict.Allowed
						: Verdict.SequenceWithoutExtrinsics;
				default:
					return Verdict.NoCameraPose;
			}
		}

		/// <summary>
		/// True when the extrinsics file holds at least one row marked valid. A desktop recording writes the file
		/// with every row invalid because it had no AR camera pose, and fusing it would reject every frame.
		/// </summary>
		internal static bool HasRecordedCameraPose(string extrinsicsPath)
		{
			if (!File.Exists(extrinsicsPath))
				return false;

			// The inspector asks on every repaint, so the answer is kept until the file changes.
			System.DateTime written = File.GetLastWriteTimeUtc(extrinsicsPath);
			if (extrinsicsPath == s_checkedPath && written == s_checkedWriteTime)
				return s_checkedHasPose;

			bool hasPose = false;
			try
			{
				foreach (string line in File.ReadLines(extrinsicsPath))
				{
					if (Recording.JsonlScan.ReadInt(line, "valid") != 0)
					{
						hasPose = true;
						break;
					}
				}
			}
			catch (IOException)
			{
				return false;
			}

			s_checkedPath = extrinsicsPath;
			s_checkedWriteTime = written;
			s_checkedHasPose = hasPose;
			return hasPose;
		}

		private static string s_checkedPath;
		private static System.DateTime s_checkedWriteTime;
		private static bool s_checkedHasPose;

		/// <summary>True when fusion is discarded for this verdict.</summary>
		internal static bool Discards(Verdict verdict)
		{
			return verdict == Verdict.NoCameraPose || verdict == Verdict.SequenceWithoutExtrinsics;
		}

		/// <summary>Why fusion is discarded, for the log and the inspector. Empty when it is not.</summary>
		internal static string Reason(Verdict verdict, ImageSource source)
		{
			switch (verdict)
			{
				case Verdict.NoCameraPose:
					return $"the {source} camera source provides no AR camera pose";
				case Verdict.SequenceWithoutExtrinsics:
					return "the recorded sequence has no recorded camera poses";
				default:
					return string.Empty;
			}
		}

		/// <summary>
		/// If the folder has no sequence.json, the latest timestamped child folder. Reads the disk only and
		/// never logs.
		/// </summary>
		internal static string ResolveSequenceDirectory(string dir)
		{
			if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
				return dir;

			if (File.Exists(Path.Combine(dir, "sequence.json")))
				return dir;

			string[] subDirs = Directory.GetDirectories(dir);
			if (subDirs.Length == 0)
				return dir;

			System.Array.Sort(subDirs, System.StringComparer.Ordinal);
			return subDirs[subDirs.Length - 1];
		}
	}
}
