using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Writes one post-lifecycle quality/status row per body and completed frame.
	/// </summary>
	/// <remarks>
	/// The writer subscribes after the body manager has interpreted the completed
	/// frame. It only reads snapshots; recorded states are never replayed into the
	/// tracker and cannot alter pose or recovery authority.
	/// </remarks>
	internal static class TrackingStatusHistoryWriter
	{
		internal const int SchemaVersion = 7;
		private static StreamWriter s_writer;
		private static XRTrackerManager s_manager;

		internal static bool IsActive => s_writer != null;

		internal static bool Start(string outputDirectory, XRTrackerManager manager)
		{
			Stop();
			if (manager == null || string.IsNullOrWhiteSpace(outputDirectory))
				return false;
			try
			{
				Directory.CreateDirectory(outputDirectory);
				s_writer = new StreamWriter(
					Path.Combine(outputDirectory, "tracking_status.jsonl"), false,
					new UTF8Encoding(false)) { AutoFlush = true };
				s_manager = manager;
				s_manager.OnTrackingLifecycleCompleted += OnTrackingLifecycleCompleted;
				return true;
			}
			catch (Exception exception)
			{
				Debug.LogError("[TrackingStatusHistoryWriter] Failed to start: " +
					exception.Message);
				Stop();
				return false;
			}
		}

		internal static void Stop()
		{
			if (s_manager != null)
				s_manager.OnTrackingLifecycleCompleted -= OnTrackingLifecycleCompleted;
			s_manager = null;
			try { s_writer?.Flush(); }
			catch (Exception exception)
			{
				Debug.LogWarning("[TrackingStatusHistoryWriter] Flush failed: " +
					exception.Message);
			}
			try { s_writer?.Dispose(); }
			catch (Exception exception)
			{
				Debug.LogWarning("[TrackingStatusHistoryWriter] Dispose failed: " +
					exception.Message);
			}
			s_writer = null;
		}

		private static void OnTrackingLifecycleCompleted(
			XRTrackerManager.TrackingFrameInstrumentationContext context)
		{
			StreamWriter writer = s_writer;
			XRTrackerManager manager = s_manager;
			if (writer == null || manager == null)
				return;
			foreach (TrackedBody body in manager.CompletedStepRegisteredBodies)
			{
				if (body == null)
					continue;
				writer.WriteLine(FormatRow(context, body));
			}
		}

		internal static string FormatRow(
			XRTrackerManager.TrackingFrameInstrumentationContext context,
			TrackedBody body)
		{
			RegistrationQualitySnapshot quality = body.RegistrationQuality;
			TemporalTrackingSnapshot status = body.TemporalTrackingStatus;
			TrackingHealthSnapshot health = body.TrackingHealth;
			var text = new StringBuilder(768);
			var culture = CultureInfo.InvariantCulture;
			text.Append("{\"schema_version\":").Append(SchemaVersion)
				.Append(",\"sequence_id\":\"").Append(Escape(context.SequenceId))
				.Append("\",\"frame\":").Append(status.FrameIndex)
				.Append(",\"step_serial\":").Append(status.StepSerial)
				.Append(",\"timestamp_ns\":").Append(context.TimestampNs)
				.Append(",\"body_id\":\"").Append(Escape(body.BodyId)).Append('"')
				.Append(",\"confidence\":\"").Append(body.Confidence).Append('"')
				.Append(",\"confidence_reason\":\"").Append(body.ConfidenceReason).Append('"')
				.Append(",\"published_pose\":{\"t\":[")
				.Append(body.transform.position.x.ToString("G9", culture)).Append(',')
				.Append(body.transform.position.y.ToString("G9", culture)).Append(',')
				.Append(body.transform.position.z.ToString("G9", culture))
				.Append("],\"q\":[")
				.Append(body.transform.rotation.x.ToString("G9", culture)).Append(',')
				.Append(body.transform.rotation.y.ToString("G9", culture)).Append(',')
				.Append(body.transform.rotation.z.ToString("G9", culture)).Append(',')
				.Append(body.transform.rotation.w.ToString("G9", culture)).Append("]}")
				.Append(",\"legacy_instant_quality\":")
				.Append(body.InstantQuality.ToString("G9", culture))
				.Append(",\"legacy_averaged_quality\":")
				.Append(body.TrackingQuality.ToString("G9", culture))
				.Append(",\"registration_schema_version\":").Append(quality.SchemaVersion)
				.Append(",\"registration_supported\":")
				.Append(quality.FeatureSupported ? '1' : '0')
				.Append(",\"registration_available\":")
				.Append(quality.ScoreAvailable ? '1' : '0')
				.Append(",\"registration_unavailable_reason\":")
				.Append((int)quality.UnavailableReason)
				.Append(",\"registration_quality\":")
				.Append(quality.Score.ToString("G9", culture))
				.Append(",\"expected_support\":").Append(quality.ExpectedSupport)
				.Append(",\"searched_support\":").Append(quality.SearchedSupport)
				.Append(",\"matched_support\":").Append(quality.MatchedSupport)
				.Append(",\"agreeing_support\":").Append(quality.AgreeingSupport)
				.Append(",\"appearance_available\":")
				.Append(quality.AppearanceEvidenceAvailable ? '1' : '0')
				.Append(",\"temporal_state\":").Append((int)status.State)
				.Append(",\"temporal_state_name\":\"").Append(status.State).Append('"')
				.Append(",\"temporal_reason\":").Append((int)status.Reason)
				.Append(",\"temporal_reason_name\":\"").Append(status.Reason).Append('"')
				.Append(",\"state_age_frames\":").Append(status.AgeFrames)
				.Append(",\"transition_frame\":").Append(status.TransitionFrameIndex)
				.Append(",\"transition_step_serial\":").Append(status.TransitionStepSerial)
				.Append(",\"recovery_phase\":").Append((int)status.RecoveryPhase)
				.Append(",\"recovery_token\":").Append(status.RecoveryToken)
				.Append(",\"mirrored_from\":\"")
				.Append(Escape(status.MirroredFromBodyId)).Append('"')
				.Append(",\"tracking_health_authority\":")
				.Append(body.UseTrackingHealth ? '1' : '0')
				.Append(",\"raw_ratio_available\":")
				.Append(health.RawAvailable ? '1' : '0')
				.Append(",\"raw_ratio\":")
				.Append(health.Raw.ToString("G9", culture))
				.Append(",\"view_expectation_available\":")
				.Append(health.ExpectationAvailable ? '1' : '0')
				.Append(",\"view_expectation\":")
				.Append(health.Expected.ToString("G9", culture))
				.Append(",\"view_expectation_source\":")
				.Append((int)health.ExpectationSource)
				.Append(",\"tracking_health_available\":")
				.Append(health.Available ? '1' : '0')
				.Append(",\"tracking_health\":")
				.Append(health.Health.ToString("G9", culture))
				.Append(",\"tracking_health_acceptance\":")
				.Append(health.AcceptanceHealth.ToString("G9", culture))
				.Append(",\"health_numerator\":")
				.Append((int)body.TrackingHealthSettings.HealthNumerator)
				.Append(",\"appearance_floor\":")
				.Append(body.TrackingHealthSettings.AppearanceFloor
					.ToString("G9", culture))
				.Append(",\"view_bank_renders\":").Append(body.ViewBankRenders)
				.Append(",\"view_bank_reuses\":").Append(body.ViewBankReuses)
				.Append(",\"view_bank_size\":").Append(body.ViewBankSize)
				.Append(",\"view_visible_fraction\":")
				.Append(body.ViewVisibleFraction.ToString("G9", culture))
				.Append(",\"appearance_agreement\":")
				.Append(quality.RawEvidence.appearance_agreement.ToString("G9", culture))
				.Append(",\"appearance_guard\":")
				.Append(health.AppearanceGuard.ToString("G9", culture))
				.Append("}");
			return text.ToString();
		}

		private static string Escape(string value)
		{
			if (string.IsNullOrEmpty(value))
				return string.Empty;
			return value.Replace("\\", "\\\\").Replace("\"", "\\\"")
				.Replace("\r", "\\r").Replace("\n", "\\n");
		}
	}
}
