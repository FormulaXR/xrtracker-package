using System;
using UnityEngine;

namespace IV.FormulaTracker
{
	public enum SmoothingMode
	{
		None,
		Lerp,
		Kalman
	}

	[Serializable]
	public class VelocityKalmanFilter
	{
		private float _position;
		private float _velocity;
		private float _p00, _p01, _p10, _p11;
		private float _processNoisePos;
		private float _processNoiseVel;
		private float _measurementNoise;
		private bool _initialized;
		private float _lastTime;

		public float Position => _position;
		public float Velocity => _velocity;

		public VelocityKalmanFilter(float processNoisePos = 0.1f, float processNoiseVel = 0.5f, float measurementNoise = 0.05f)
		{
			_processNoisePos = processNoisePos;
			_processNoiseVel = processNoiseVel;
			_measurementNoise = measurementNoise;
			Reset();
		}

		public void SetParameters(float processNoisePos, float processNoiseVel, float measurementNoise)
		{
			_processNoisePos = processNoisePos;
			_processNoiseVel = processNoiseVel;
			_measurementNoise = measurementNoise;
		}

		public void Reset()
		{
			_initialized = false;
			_position = 0f;
			_velocity = 0f;
			_p00 = 1f; _p01 = 0f;
			_p10 = 0f; _p11 = 1f;
			_lastTime = 0f;
		}

		public float Update(float measurement, float currentTime)
		{
			if (!_initialized)
			{
				_position = measurement;
				_velocity = 0f;
				_lastTime = currentTime;
				_initialized = true;
				return _position;
			}

			float dt = currentTime - _lastTime;
			if (dt <= 0f) dt = 0.016f;
			_lastTime = currentTime;

			float predictedPos = _position + _velocity * dt;
			float predictedVel = _velocity;

			float q00 = _processNoisePos * dt;
			float q11 = _processNoiseVel * dt;

			float newP00 = _p00 + dt * (_p10 + _p01) + dt * dt * _p11 + q00;
			float newP01 = _p01 + dt * _p11;
			float newP10 = _p10 + dt * _p11;
			float newP11 = _p11 + q11;

			float s = newP00 + _measurementNoise;
			float k0 = newP00 / s;
			float k1 = newP10 / s;

			float innovation = measurement - predictedPos;

			_position = predictedPos + k0 * innovation;
			_velocity = predictedVel + k1 * innovation;

			_p00 = (1f - k0) * newP00;
			_p01 = (1f - k0) * newP01;
			_p10 = newP10 - k1 * newP00;
			_p11 = newP11 - k1 * newP01;

			return _position;
		}
	}

	[Serializable]
	public class PoseKalmanFilter
	{
		private VelocityKalmanFilter _posX, _posY, _posZ;
		private Quaternion _smoothedRotation = Quaternion.identity;
		private Vector3 _angularVelocity;
		private float _rotationSmoothness;
		private float _angularVelocityDecay;
		private bool _initialized;
		private Quaternion _lastRotation = Quaternion.identity;
		private float _lastTime;

		public Vector3 PositionVelocity => new Vector3(_posX.Velocity, _posY.Velocity, _posZ.Velocity);
		public Vector3 AngularVelocity => _angularVelocity;

		public PoseKalmanFilter(
			float posProcessNoise = 0.1f,
			float posVelocityNoise = 0.5f,
			float posMeasurementNoise = 0.05f,
			float rotationSmoothness = 0.15f)
		{
			_posX = new VelocityKalmanFilter(posProcessNoise, posVelocityNoise, posMeasurementNoise);
			_posY = new VelocityKalmanFilter(posProcessNoise, posVelocityNoise, posMeasurementNoise);
			_posZ = new VelocityKalmanFilter(posProcessNoise, posVelocityNoise, posMeasurementNoise);
			_rotationSmoothness = rotationSmoothness;
			_angularVelocityDecay = 0.9f;
			_initialized = false;
		}

		public void SetPositionParameters(float processNoise, float velocityNoise, float measurementNoise)
		{
			_posX.SetParameters(processNoise, velocityNoise, measurementNoise);
			_posY.SetParameters(processNoise, velocityNoise, measurementNoise);
			_posZ.SetParameters(processNoise, velocityNoise, measurementNoise);
		}

		public void SetRotationSmoothness(float smoothness) => _rotationSmoothness = smoothness;

		public void Reset()
		{
			_posX.Reset();
			_posY.Reset();
			_posZ.Reset();
			_smoothedRotation = Quaternion.identity;
			_angularVelocity = Vector3.zero;
			_lastRotation = Quaternion.identity;
			_initialized = false;
		}

		public (Vector3 position, Quaternion rotation) Update(Vector3 measuredPos, Quaternion measuredRot, float time, float deltaTime)
		{
			if (_initialized && Quaternion.Dot(_smoothedRotation, measuredRot) < 0)
				measuredRot = new Quaternion(-measuredRot.x, -measuredRot.y, -measuredRot.z, -measuredRot.w);

			if (!_initialized)
			{
				_posX.Update(measuredPos.x, time);
				_posY.Update(measuredPos.y, time);
				_posZ.Update(measuredPos.z, time);
				_smoothedRotation = measuredRot;
				_lastRotation = measuredRot;
				_lastTime = time;
				_initialized = true;
				return (measuredPos, measuredRot);
			}

			Vector3 filteredPos = new Vector3(
				_posX.Update(measuredPos.x, time),
				_posY.Update(measuredPos.y, time),
				_posZ.Update(measuredPos.z, time)
			);

			Quaternion deltaRot = measuredRot * Quaternion.Inverse(_lastRotation);
			deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
			if (angle > 180f) angle -= 360f;

			if (deltaTime > 0.0001f && axis.sqrMagnitude > 0.0001f)
			{
				Vector3 measuredAngularVel = axis.normalized * (angle * Mathf.Deg2Rad / deltaTime);
				_angularVelocity = Vector3.Lerp(_angularVelocity, measuredAngularVel, 0.3f);
			}
			else
			{
				_angularVelocity *= _angularVelocityDecay;
			}

			float t = 1f - Mathf.Exp(-deltaTime / Mathf.Max(_rotationSmoothness, 0.001f));
			_smoothedRotation = Quaternion.Slerp(_smoothedRotation, measuredRot, t);
			_smoothedRotation.Normalize();

			_lastRotation = measuredRot;
			_lastTime = time;

			return (filteredPos, _smoothedRotation);
		}
	}
}
