using System;
using System.Runtime.InteropServices;

namespace IV.FormulaTracker.Validation
{
	/// <summary>
	/// Validation zone shape types
	/// </summary>
	public enum FTZoneShape
	{
		Cylinder = 0,
		Box = 1,
		Quad = 2
	}

	/// <summary>
	/// Validation readiness states
	/// </summary>
	public enum FTValidationReadiness
	{
		Ready = 0,
		ZoneNotVisible = 1,
		ZoneOutOfFrame = 2,
		TrackingUnstable = 3,
		TrackingLost = 4
	}

	/// <summary>
	/// Histogram comparison methods
	/// </summary>
	public enum FTHistogramCompareMethod
	{
		Correlation = 0,     // Higher is better, range [-1, 1]
		ChiSquare = 1,       // Lower is better, range [0, inf)
		Intersection = 2,    // Higher is better
		Bhattacharyya = 3    // Lower is better, range [0, 1]
	}

	/// <summary>
	/// Validation zone error codes
	/// </summary>
	public static class ValidationErrorCode
	{
		public const int ZONE_NOT_FOUND = -10;
		public const int VALIDATOR_NOT_FOUND = -11;
		public const int ZONE_EXISTS = -12;
		public const int VALIDATOR_EXISTS = -13;
		public const int INVALID_IMAGE = -14;
	}

	/// <summary>
	/// P/Invoke declarations for validation zone APIs
	/// </summary>
	public static class ValidationBridge
	{
#if UNITY_IOS && !UNITY_EDITOR
		private const string DLL_NAME = "__Internal";
#else
		private const string DLL_NAME = "formula_tracker";
#endif

		#region Zone Management

		/// <summary>
		/// Create a validation zone attached to a tracked body.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_CreateValidationZone(ref FTValidationZoneConfig config);

		/// <summary>
		/// Update validation zone parameters.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_UpdateValidationZone(
			string zoneId,
			float posX, float posY, float posZ,
			float rotX, float rotY, float rotZ, float rotW,
			float dimX, float dimY, float dimZ,
			float trackingQualityThreshold);

		/// <summary>
		/// Remove a validation zone.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_RemoveValidationZone(string zoneId);

		#endregion

		#region Validator Management

		/// <summary>
		/// Add a template validator to a zone.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_AddTemplateValidator(
			string zoneId,
			ref FTTemplateValidatorConfig config,
			IntPtr templateRGBA,
			int width,
			int height);

		/// <summary>
		/// Add a histogram validator to a zone.
		/// Compares color distribution instead of pixel positions.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_AddHistogramValidator(
			string zoneId,
			ref FTHistogramValidatorConfig config,
			IntPtr templateRGBA,
			int width,
			int height);

		/// <summary>
		/// Remove a validator from a zone.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_RemoveValidator(string zoneId, string validatorId);

		#endregion

		#region Validation Processing

		/// <summary>
		/// Process validation for a single zone.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_ProcessValidation(
			string zoneId,
			float trackingQuality,
			out FTZoneValidationResult result);

		/// <summary>
		/// Process validation for all zones.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_ProcessAllValidations(
			[Out] FTZoneValidationResult[] results,
			int maxResults,
			out int numResults);

		/// <summary>
		/// Check if a zone is ready for validation (without running validators).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_CheckZoneReadiness(
			string zoneId,
			float trackingQuality,
			out FTReadinessResult result);

		/// <summary>
		/// Get individual validator result (call after FT_ProcessValidation).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetValidatorResult(
			string zoneId,
			int validatorIndex,
			out FTValidatorResult result);

		#endregion

		#region Debug / Visualization

		/// <summary>
		/// Get the sampled image for a zone (for debugging/visualization).
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
		public static extern int FT_GetSampledImage(
			string zoneId,
			IntPtr rgbData,
			int maxWidth,
			int maxHeight,
			out int actualWidth,
			out int actualHeight);

		#endregion

		#region Lifecycle

		/// <summary>
		/// Clear all validation zones.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern void FT_ClearValidationZones();

		/// <summary>
		/// Get number of registered validation zones.
		/// </summary>
		[DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
		public static extern int FT_GetValidationZoneCount();

		#endregion
	}

	#region Configuration Structs

	/// <summary>
	/// Validation zone creation parameters
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTValidationZoneConfig
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string zone_id;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string body_name;

		public int shape;  // FTZoneShape

		// Local pose relative to body (quaternion + position)
		public float pos_x, pos_y, pos_z;
		public float rot_x, rot_y, rot_z, rot_w;

		// Dimensions (shape-specific)
		public float dim_x, dim_y, dim_z;

		// Minimum tracking quality to run validation
		public float tracking_quality_threshold;

		public static FTValidationZoneConfig Default()
		{
			return new FTValidationZoneConfig
			{
				zone_id = "",
				body_name = "",
				shape = (int)FTZoneShape.Cylinder,
				pos_x = 0f, pos_y = 0f, pos_z = 0f,
				rot_x = 0f, rot_y = 0f, rot_z = 0f, rot_w = 1f,
				dim_x = 0.02f, dim_y = 0.05f, dim_z = 0f,
				tracking_quality_threshold = 0.5f
			};
		}
	}

	/// <summary>
	/// Template validator creation parameters
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTTemplateValidatorConfig
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string validator_id;

		public int use_alpha_mask;

		public static FTTemplateValidatorConfig Default()
		{
			return new FTTemplateValidatorConfig
			{
				validator_id = "",
				use_alpha_mask = 1
			};
		}
	}

	/// <summary>
	/// Histogram validator creation parameters
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTHistogramValidatorConfig
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string validator_id;

		public int method;       // FTHistogramCompareMethod
		public int use_hue_only; // Use only Hue channel (lighting-robust)
		public int num_bins;     // Number of histogram bins per channel

		public static FTHistogramValidatorConfig Default()
		{
			return new FTHistogramValidatorConfig
			{
				validator_id = "",
				method = (int)FTHistogramCompareMethod.Correlation,
				use_hue_only = 0,
				num_bins = 32
			};
		}
	}

	/// <summary>
	/// Readiness check result
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTReadinessResult
	{
		public int status;  // FTValidationReadiness
		public float visibility_score;

		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
		public string message;

		public FTValidationReadiness Status => (FTValidationReadiness)status;
		public bool IsReady => status == (int)FTValidationReadiness.Ready;
	}

	/// <summary>
	/// Single validator result
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTValidatorResult
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string validator_id;

		public float confidence;
		public float offset_x;
		public float offset_y;
		public float detected_rotation;
	}

	/// <summary>
	/// Full zone validation result
	/// </summary>
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
	public struct FTZoneValidationResult
	{
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
		public string zone_id;

		public FTReadinessResult readiness;
		public int validation_attempted;
		public int num_validators;

		public bool ValidationAttempted => validation_attempted != 0;
	}

	#endregion
}
