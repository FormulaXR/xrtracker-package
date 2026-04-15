using System;

namespace IV.FormulaTracker
{
	/// <summary>
	/// License tiers. Mirrors C++ LicenseTier (OEM maps to Commercial on the native side).
	/// </summary>
	public enum LicenseTier
	{
		None = 0,
		[Obsolete("Free tier deprecated. Use Developer tier.")] Free = 1,
		[Obsolete("Trial tier deprecated. Use Developer tier.")] Trial = 2,
		Developer = 3,
		Commercial = 4,
	}

	/// <summary>
	/// License validation status. Mirrors C++ LicenseStatus.
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
	/// Specifies which degrees of freedom (DOF) are free to be optimized for attached bodies.
	/// None means rigid attachment (pose follows parent). Any flags enabled means active tracking.
	/// Bits: 0=rot_x, 1=rot_y, 2=rot_z, 3=trans_x, 4=trans_y, 5=trans_z
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
		/// <summary>Native OpenCV webcam capture. Requires calibration file.</summary>
		Native = 0,
		/// <summary>Unity feeds frames (AR Foundation). No calibration file needed.</summary>
		Injected = 1,
		/// <summary>Intel RealSense SDK capture (color + depth). No calibration file needed.</summary>
		RealSense = 2,
		/// <summary>Recorded image sequence. C++ reads PNGs directly — no C# texture overhead.</summary>
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