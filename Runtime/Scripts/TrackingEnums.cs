using System;

namespace IV.FormulaTracker
{
	/// <summary>
	/// License tiers. The values match the native plugin, which reports an OEM license as Commercial.
	/// </summary>
	public enum LicenseTier
	{
		None = 0,
		[Obsolete("The Free tier is retired. Use the Developer tier.")] Free = 1,
		[Obsolete("The Trial tier is retired. Use the Developer tier.")] Trial = 2,
		Developer = 3,
		Commercial = 4,
	}

	/// <summary>
	/// License validation status. The values match the native plugin.
	/// </summary>
	public enum LicenseStatus
	{
		NotSet = 0,
		Valid = 1,
		Expired = 2,
		InvalidSignature = 3,
		MachineIdMismatch = 4,
		AppIdMismatch = 5,
		FormatError = 6,
	}

	/// <summary>
	/// Specifies which degrees of freedom are free to be optimized for attached bodies.
	/// None means rigid attachment (the pose follows the parent). Any flag that is set means active tracking.
	/// The flags use bit 0 for rotation X, bit 1 for rotation Y, bit 2 for rotation Z, bit 3 for translation X, bit 4 for translation Y and bit 5 for translation Z.
	/// </summary>
	[Flags]
	public enum TrackedMotion
	{
		None = 0,
		RotationX = 1 << 0,
		RotationY = 1 << 1,
		RotationZ = 1 << 2,
		TranslationX = 1 << 3,
		TranslationY = 1 << 4,
		TranslationZ = 1 << 5
	}

	/// <summary>
	/// Determines how the initial pose is computed for detection.
	/// </summary>
	public enum InitialPoseSource
	{
		/// <summary>
		/// Use the object's current position in the scene.
		/// The pose is calculated relative to the main camera at startup.
		/// </summary>
		ScenePosition,

		/// <summary>
		/// Use a specified viewpoint transform.
		/// The object's pose is calculated relative to the viewpoint.
		/// </summary>
		Viewpoint
	}

	/// <summary>
	/// Camera input source for XRTrackerManager.
	/// </summary>
	public enum ImageSource
	{
		/// <summary>Native webcam capture. A calibration file gives the best accuracy, and without one the camera intrinsics are estimated from the field of view.</summary>
		Native = 0,
		/// <summary>Unity feeds frames (AR Foundation). No calibration file needed.</summary>
		Injected = 1,
		/// <summary>Intel RealSense SDK capture (color + depth). No calibration file needed.</summary>
		RealSense = 2,
		/// <summary>Recorded image sequence. The native plugin reads the PNG files directly, so no texture copy is needed in managed code.</summary>
		Sequence = 3
	}

	/// <summary>RealSense color camera resolution options.</summary>
	public enum RealSenseColorResolution
	{
		/// <summary>640x480 @ 60fps</summary>
		Resolution_640x480_60fps = 0,
		/// <summary>960x540 @ 60fps (default)</summary>
		Resolution_960x540_60fps = 1,
		/// <summary>1280x720 @ 30fps (HD)</summary>
		Resolution_1280x720_30fps = 2,
		/// <summary>1920x1080 @ 30fps (Full HD)</summary>
		Resolution_1920x1080_30fps = 3
	}

	/// <summary>RealSense depth camera resolution options.</summary>
	public enum RealSenseDepthResolution
	{
		/// <summary>480x270 @ 90fps</summary>
		Resolution_480x270_90fps = 0,
		/// <summary>640x480 @ 90fps</summary>
		Resolution_640x480_90fps = 1,
		/// <summary>848x480 @ 90fps (default)</summary>
		Resolution_848x480_90fps = 2,
		/// <summary>1280x720 @ 30fps (HD)</summary>
		Resolution_1280x720_30fps = 3
	}
}