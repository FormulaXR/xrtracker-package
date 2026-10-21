namespace IV.FormulaTracker.Recording
{
	/// <summary>
	/// Starts and stops one live recording together with the AR session state
	/// sidecar that a device capture is checked against.
	/// </summary>
	public static class RecordingSession
	{
		/// <summary>Whether the native recorder is currently accepting frames.</summary>
		public static bool IsRecording => FTBridge.FT_IsRecording() != 0;

		/// <summary>Number of color frames accepted by the native recorder.</summary>
		public static int FrameCount => FTBridge.FT_GetRecordedFrameCount();

		/// <summary>
		/// Starts a recording in <paramref name="outputDirectory"/>. Returns an
		/// <see cref="FTErrorCode"/> value.
		/// </summary>
		public static int Start(string outputDirectory)
		{
			if (string.IsNullOrWhiteSpace(outputDirectory))
				return FTErrorCode.INVALID_PARAMETER;
			if (IsRecording)
				return FTErrorCode.ALREADY_INITIALIZED;

			XRTrackerManager.WaitForTrackingStepToReturn();
			int result = FTBridge.FT_StartRecording(outputDirectory);
			if (result != FTErrorCode.OK)
				return result;

			TrackingStatusHistoryWriter.Start(outputDirectory, XRTrackerManager.Instance);

#if HAS_AR_FOUNDATION
			ARSessionStateWriter.Start(outputDirectory);
#endif
			return FTErrorCode.OK;
		}

		/// <summary>
		/// Stops the current recording, drains its frame queue, and closes every
		/// sidecar. Returns the committed frame count or a negative error code.
		/// </summary>
		public static int Stop()
		{
			XRTrackerManager.WaitForTrackingStepToReturn();
			int result = FTBridge.FT_StopRecording();
			TrackingStatusHistoryWriter.Stop();
#if HAS_AR_FOUNDATION
			ARSessionStateWriter.Stop();
#endif
			return result;
		}
	}
}
