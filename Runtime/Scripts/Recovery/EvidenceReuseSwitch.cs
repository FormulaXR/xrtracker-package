using System;
using UnityEngine;

namespace IV.FormulaTracker
{
	/// <summary>
	/// Tells the plugin which reuse of work between pose evaluations the options ask for, and nothing else. It makes
	/// no call while the cache is off, one call when the answer changes, and one call back to zero when the cache is
	/// switched off after it was on. A plugin that does not offer the cache (an older build of a platform plugin
	/// refuses the call or does not export it) is asked once, the refusal is logged once, and the plugin is never asked
	/// again for the same answer, so a cache that is on by default costs such a plugin nothing on a row.
	/// </summary>
	internal sealed class EvidenceReuseSwitch
	{
		private readonly Func<int, int, bool> _configure;
		private readonly Func<bool, bool> _timing;
		private readonly Action<string> _log;
		private int _appliedFlags;
		private bool _appliedTiming;
		private bool _refusalLogged;

		/// <summary>The line written once when the plugin does not take the cache.</summary>
		internal const string REFUSED_MESSAGE =
			"[XRTracker] The loaded plugin does not offer the evidence site cache, so recovery evaluates every pose from the start. " +
			"Nothing else changes, and the plugin is not asked again.";

		/// <summary>Whether the plugin refused, or failed, the last request that was not for zero.</summary>
		public bool Refused { get; private set; }

		/// <summary>How many times the plugin was asked about the cache.</summary>
		public int ConfigureCalls { get; private set; }

		/// <summary>How many times the plugin was asked about the start timing.</summary>
		public int TimingCalls { get; private set; }

		/// <summary>The flags the plugin was last given. Zero is everything off.</summary>
		public int AppliedFlags => _appliedFlags;

		/// <summary>The switch that talks to the plugin.</summary>
		public EvidenceReuseSwitch() : this(FTBridge.TryConfigureEvidenceSiteCache, FTBridge.TrySetEvaluationStartTiming)
		{
		}

		/// <summary>A switch that talks to the given calls, for a test.</summary>
		public EvidenceReuseSwitch(Func<int, int, bool> configure, Func<bool, bool> timing, Action<string> log = null)
		{
			_configure = configure;
			_timing = timing;
			_log = log ?? Debug.Log;
		}

		/// <summary>
		/// The flags the plugin is given for the options. The cache option turns on the sites and the search image of
		/// the frame, the verify option adds the comparison, and verify alone is nothing.
		/// </summary>
		internal static int FlagsFor(bool? cache, bool? verify)
		{
			if (cache != true)
				return 0;
			int flags = EvidenceCacheFlags.Sites | EvidenceCacheFlags.SearchImage;
			flags |= TrackerDefaults.EVIDENCE_CACHE_SKIP_FIRST_SEARCH ? EvidenceCacheFlags.SkipFirstSearch : 0;
			if (verify == true)
				flags |= EvidenceCacheFlags.Verify;
			return flags;
		}

		/// <summary>Brings the plugin to what the options ask for. Safe to call on every row.</summary>
		public void Sync(bool? cache, bool? verify, bool? startTiming)
		{
			int wanted = FlagsFor(cache, verify);
			if (wanted != _appliedFlags)
			{
				++ConfigureCalls;
				// A plugin that refuses the call, or has no such entry point, is asked once and not again on every row.
				bool accepted;
				try
				{
					accepted = _configure(wanted, TrackerDefaults.EVIDENCE_SITE_CACHE_CAPACITY);
				}
				catch (Exception)
				{
					accepted = false;
				}
				_appliedFlags = wanted;
				Refused = wanted != 0 && !accepted;
				if (Refused && !_refusalLogged)
				{
					_refusalLogged = true;
					_log(REFUSED_MESSAGE);
				}
			}
			bool wantedTiming = startTiming == true;
			if (wantedTiming != _appliedTiming)
			{
				++TimingCalls;
				_timing(wantedTiming);
				_appliedTiming = wantedTiming;
			}
		}
	}
}
