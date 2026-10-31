export const defaults = Object.freeze({ positionZero: 0, angleZero: 0, positionSign: 1, angleSign: 1 });

export function validCalibration(c) {
  return c && Number.isFinite(c.positionZero) && Number.isFinite(c.angleZero)
    && [1, -1].includes(c.positionSign) && [1, -1].includes(c.angleSign);
}

export function mapSample(sample, calibration = defaults) {
  if (!validCalibration(calibration) || !sample || sample.valid !== true
      || !['position', 'time', 'sequence'].every(k => Number.isFinite(sample[k]))
      || !Number.isSafeInteger(sample.sequence) || sample.sequence < 0 || sample.time < 0
      || ['angle','rpm','force','torque'].some(k => sample[k] != null && !Number.isFinite(sample[k]))) return null;
  const displacement = (sample.position - calibration.positionZero) * calibration.positionSign;
  const rotation = sample.angle == null ? null : (sample.angle - calibration.angleZero) * calibration.angleSign;
  // One model unit is one mm. Rotation is reduced only for rendering, never integrated from speed.
  return { displacement, carriageX: displacement, rotation: rotation == null ? null : rotation % (2 * Math.PI),
    outOfRange: displacement < -1 || displacement > 101, sequence: sample.sequence, time: sample.time };
}

export class PositionFeed {
  constructor() { this.reset(); }
  reset() { this.sample = null; this.sequence = -1; this.time = -Infinity; this.receivedAt = -Infinity; this.invalid = false; }
  accept(sample, now) {
    if (!mapSample(sample)) { this.invalid = true; return false; }
    if (sample.sequence <= this.sequence || sample.time < this.time) return false;
    this.sample = sample; this.sequence = sample.sequence; this.time = sample.time;
    this.receivedAt = now; this.invalid = false; return true;
  }
  status(state, now) {
    if (state === 'Disconnected') return '未连接';
    if (state === 'Fault') return '故障 · 位置保持';
    if (!this.sample) return '等待位置数据';
    if (this.invalid) return '无效数据 · 位置保持';
    if (state === 'Paused') return '已暂停 · 位置保持';
    if (state === 'Running' && now - this.receivedAt > 1000) return '数据超时 · 位置保持';
    return state === 'Running' ? '位置同步中' : '已停止 · 位置保持';
  }
}
