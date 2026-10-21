using System.Collections.Generic;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// What one body's site-funnel ratio normally reaches, kept per viewpoint.
	/// </summary>
	/// <remarks>
	/// <para>One body seen from two directions produces two different ratios while
	/// tracking equally well, so the divisor that makes the ratio comparable has to
	/// be held per viewpoint rather than per body. Entries are keyed by the
	/// viewpoint a pose stands at and retrieved by nearest bearing within a cone,
	/// with a body-wide entry underneath for a direction nothing has been stored
	/// near.</para>
	/// <para>The store only ever learns from frames the caller declares healthy.
	/// A pose that is drifting still produces a ratio, and letting that ratio into
	/// the average would lower the bar the same drift is being measured against
	/// until nothing is left to detect. The caller owns that decision; this type
	/// records what it is told and nothing else.</para>
	/// </remarks>
	internal sealed class ViewExpectationStore
	{
		/// <summary>One viewpoint's expectation and how it came to be.</summary>
		public readonly struct Entry
		{
			internal Entry(RecoveryViewDescriptor view, float expected, int samples,
				int lastFrame, ViewExpectationSource source)
			{
				View = view;
				Expected = expected;
				Samples = samples;
				LastFrame = lastFrame;
				Source = source;
			}

			/// <summary>The viewpoint this entry was recorded at.</summary>
			public RecoveryViewDescriptor View { get; }

			/// <summary>The ratio this viewpoint normally reaches.</summary>
			public float Expected { get; }

			/// <summary>How many frames have contributed to it.</summary>
			public int Samples { get; }

			/// <summary>The last frame that contributed.</summary>
			public int LastFrame { get; }

			/// <summary>How the entry was first established.</summary>
			public ViewExpectationSource Source { get; }
		}

		private readonly List<Entry> _entries = new List<Entry>();
		private Entry _bodyWide;
		private bool _hasBodyWide;

		/// <summary>How many viewpoints carry an expectation.</summary>
		public int Count => _entries.Count;

		/// <summary>Whether a direction with no nearby entry can still be answered.</summary>
		public bool HasBodyWideExpectation => _hasBodyWide;

		/// <summary>The expectation used when no stored viewpoint is near enough.</summary>
		public Entry BodyWideExpectation => _bodyWide;

		/// <summary>Reads one stored viewpoint. Order is insertion order.</summary>
		public Entry EntryAt(int index) => _entries[index];

		/// <summary>Drops every expectation. A body that is being started over has none.</summary>
		public void Clear()
		{
			_entries.Clear();
			_bodyWide = default;
			_hasBodyWide = false;
		}

		/// <summary>
		/// Finds what <paramref name="view"/> is expected to reach.
		/// </summary>
		/// <param name="view">The viewpoint the current pose stands at.</param>
		/// <param name="bearingDegrees">Widest bearing a stored entry may sit at.</param>
		/// <param name="rangeRatio">Widest range ratio, in either direction.</param>
		/// <param name="expected">Receives the expected ratio.</param>
		/// <param name="source">Receives where that expectation came from.</param>
		/// <returns>False when nothing covers this viewpoint.</returns>
		public bool TryLookup(in RecoveryViewDescriptor view, float bearingDegrees,
			float rangeRatio, out float expected, out ViewExpectationSource source)
		{
			expected = 0f;
			source = ViewExpectationSource.None;
			if (view.IsValid)
			{
				int nearest = FindNearest(view, bearingDegrees, rangeRatio);
				if (nearest >= 0)
				{
					expected = _entries[nearest].Expected;
					source = _entries[nearest].Source;
					return true;
				}
			}
			if (!_hasBodyWide)
				return false;
			expected = _bodyWide.Expected;
			source = _bodyWide.Source;
			return true;
		}

		/// <summary>
		/// Establishes the expectation for <paramref name="view"/>, replacing any
		/// entry already covering it.
		/// </summary>
		/// <remarks>
		/// A seed is a fresh statement rather than a correction, so it overwrites
		/// instead of averaging. The body-wide entry is seeded alongside it when
		/// nothing has established one yet, so the first view a body acquires from
		/// answers directions nothing has been stored near.
		/// </remarks>
		public void Seed(in RecoveryViewDescriptor view, float expected, int frame,
			ViewExpectationSource source, float bearingDegrees, float rangeRatio)
		{
			if (!view.IsValid || !IsUsable(expected))
				return;
			var entry = new Entry(view, expected, 1, frame, source);
			int nearest = FindNearest(view, bearingDegrees, rangeRatio);
			if (nearest >= 0)
				_entries[nearest] = entry;
			else
				_entries.Add(entry);
			if (!_hasBodyWide)
			{
				_bodyWide = new Entry(default, expected, 1, frame, source);
				_hasBodyWide = true;
			}
		}

		/// <summary>
		/// Establishes the expectation for <paramref name="view"/> only when
		/// nothing already covers it.
		/// </summary>
		/// <returns>False when an entry already describes this viewpoint.</returns>
		public bool TrySeedIfAbsent(in RecoveryViewDescriptor view, float expected,
			int frame, ViewExpectationSource source, float bearingDegrees,
			float rangeRatio)
		{
			if (!view.IsValid || !IsUsable(expected) ||
				FindNearest(view, bearingDegrees, rangeRatio) >= 0)
			{
				return false;
			}
			Seed(view, expected, frame, source, bearingDegrees, rangeRatio);
			return true;
		}

		/// <summary>
		/// Folds one healthy frame's ratio into the entry covering
		/// <paramref name="view"/> and into the body-wide entry.
		/// </summary>
		/// <remarks>
		/// The average is deliberately slow. What it is tracking is how much of a
		/// body a viewpoint can see, which changes with lighting and wear over many
		/// frames and not within one; a fast average would instead track the frame
		/// noise it is meant to see through.
		/// </remarks>
		public void Update(in RecoveryViewDescriptor view, float raw, int frame,
			float alpha, float bearingDegrees, float rangeRatio)
		{
			if (!view.IsValid || !IsUsable(raw))
				return;
			alpha = Mathf.Clamp01(alpha);
			int nearest = FindNearest(view, bearingDegrees, rangeRatio);
			if (nearest < 0)
			{
				Seed(view, raw, frame, ViewExpectationSource.Learned, bearingDegrees,
					rangeRatio);
				return;
			}
			Entry entry = _entries[nearest];
			_entries[nearest] = new Entry(entry.View,
				Mathf.Lerp(entry.Expected, raw, alpha),
				entry.Samples < int.MaxValue ? entry.Samples + 1 : entry.Samples,
				frame, entry.Source);
			_bodyWide = _hasBodyWide
				? new Entry(default, Mathf.Lerp(_bodyWide.Expected, raw, alpha),
					_bodyWide.Samples < int.MaxValue ? _bodyWide.Samples + 1 : _bodyWide.Samples,
					frame, _bodyWide.Source)
				: new Entry(default, raw, 1, frame, ViewExpectationSource.Learned);
			_hasBodyWide = true;
		}

		private int FindNearest(in RecoveryViewDescriptor view, float bearingDegrees,
			float rangeRatio)
		{
			int nearest = -1;
			float nearestBearing = float.MaxValue;
			for (int index = 0; index < _entries.Count; ++index)
			{
				RecoveryViewDescriptor stored = _entries[index].View;
				if (!stored.IsValid)
					continue;
				float bearing = RecoveryViewDescriptor.BearingDegrees(view, stored);
				if (float.IsNaN(bearing) || bearing > bearingDegrees ||
					bearing >= nearestBearing)
				{
					continue;
				}
				float ratio = RecoveryViewDescriptor.RangeRatio(view, stored);
				if (float.IsNaN(ratio) || !(ratio > 0f))
					continue;
				float widest = ratio >= 1f ? ratio : 1f / ratio;
				if (widest > rangeRatio)
					continue;
				nearest = index;
				nearestBearing = bearing;
			}
			return nearest;
		}

		private static bool IsUsable(float value) =>
			value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
