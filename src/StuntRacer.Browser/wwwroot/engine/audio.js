import { clamp, random } from './math.js';

export class RacingAudio {
  muted = false;
  lastImpact = 0;
  lastCountdown = -1;
  lastGear = 1;
  lastStall = false;
  events = 0;

  async unlock() {
    if (!this.context) this.create();
    await this.context.resume();
    if (this.context.state !== 'running') throw new Error('Audio could not start. Use the sound button to try again.');
  }

  create() {
    const Audio = globalThis.AudioContext ?? globalThis.webkitAudioContext;
    if (!Audio) throw new Error('This browser does not support Web Audio.');
    const context = this.context = new Audio({ latencyHint: 'interactive' });
    const compressor = context.createDynamicsCompressor();
    compressor.threshold.value = -18;
    compressor.ratio.value = 5;
    this.master = context.createGain();
    this.master.gain.value = this.muted ? 0 : 0.55;
    this.analyser = context.createAnalyser();
    this.analyser.fftSize = 256;
    this.master.connect(compressor).connect(this.analyser).connect(context.destination);
    this.engineGain = context.createGain();
    this.engineGain.gain.value = 0;
    this.engineFilter = context.createBiquadFilter();
    this.engineFilter.type = 'lowpass';
    this.engineFilter.frequency.value = 700;
    this.engineGain.connect(this.engineFilter).connect(this.master);
    this.oscillators = [1, 2, 0.5].map((multiple, index) => {
      const oscillator = context.createOscillator(), gain = context.createGain();
      oscillator.type = index === 1 ? 'triangle' : 'sawtooth';
      oscillator.frequency.value = 38 * multiple;
      oscillator.detune.value = index === 2 ? -7 : 3;
      gain.gain.value = [0.42, 0.28, 0.17][index];
      oscillator.connect(gain).connect(this.engineGain);
      oscillator.start();
      return oscillator;
    });
    const rng = random(1989), buffer = context.createBuffer(1, context.sampleRate * 2, context.sampleRate);
    const samples = buffer.getChannelData(0);
    for (let i = 0; i < samples.length; i++) samples[i] = rng() * 2 - 1;
    this.noiseBuffer = buffer;
    this.wind = this.noiseLoop('lowpass', 1700);
    this.boost = this.noiseLoop('bandpass', 700);
    this.skid = this.noiseLoop('bandpass', 2100);
  }

  noiseLoop(type, frequency) {
    const context = this.context, source = context.createBufferSource();
    source.buffer = this.noiseBuffer;
    source.loop = true;
    const filter = context.createBiquadFilter(), gain = context.createGain();
    filter.type = type;
    filter.frequency.value = frequency;
    gain.gain.value = 0;
    source.connect(filter).connect(gain).connect(this.master);
    source.start();
    return { source, filter, gain };
  }

  setMuted(value) {
    this.muted = value;
    if (this.context) this.master.gain.setTargetAtTime(value ? 0 : 0.55, this.context.currentTime, 0.04);
  }

  tone(frequency, duration = 0.12) {
    if (!this.context || this.context.state !== 'running') return;
    const oscillator = this.context.createOscillator(), gain = this.context.createGain();
    const now = this.context.currentTime;
    oscillator.type = 'sine';
    oscillator.frequency.value = frequency;
    gain.gain.setValueAtTime(0.17, now);
    gain.gain.exponentialRampToValueAtTime(0.001, now + duration);
    oscillator.connect(gain).connect(this.master);
    oscillator.onended = () => { oscillator.disconnect(); gain.disconnect(); };
    oscillator.start();
    oscillator.stop(now + duration);
    this.events++;
  }

  impact(amount) {
    if (!this.context || this.context.state !== 'running') return;
    const now = this.context.currentTime, source = this.context.createBufferSource();
    const filter = this.context.createBiquadFilter(), gain = this.context.createGain();
    source.buffer = this.noiseBuffer;
    filter.type = 'lowpass';
    filter.frequency.value = 280 + amount * 700;
    gain.gain.setValueAtTime(0.12 + amount * 0.5, now);
    gain.gain.exponentialRampToValueAtTime(0.001, now + 0.12 + amount * 0.28);
    source.connect(filter).connect(gain).connect(this.master);
    source.onended = () => { source.disconnect(); filter.disconnect(); gain.disconnect(); };
    source.start();
    source.stop(now + 0.45);
    this.events++;
  }

  update(state, input) {
    if (!this.context || this.context.state !== 'running') return;
    const now = this.context.currentTime, car = state.player, running = state.phase === 'racing';
    const aircraft = state.flight?.aircraft;
    const speed = Math.abs(car.speed), gear = Math.min(5, 1 + Math.floor(speed / 12));
    const power = aircraft?.throttle ?? input.throttle;
    const rpm = aircraft ? 100 + power * 160 : 35 + (speed % 12) * 4 + power * 18;
    for (let i = 0; i < this.oscillators.length; i++) {
      this.oscillators[i].frequency.setTargetAtTime(rpm * [1, 2, 0.5][i], now, 0.07);
    }
    this.engineGain.gain.setTargetAtTime(running ? aircraft ? 0.06 + power * 0.12 : 0.13 + power * 0.1 : 0, now, 0.07);
    this.engineFilter.frequency.setTargetAtTime(550 + rpm * 13 + power * 600, now, 0.06);
    this.wind.gain.gain.setTargetAtTime(running ? clamp(speed / (aircraft ? 180 : 80), 0, 1) ** 2 * 0.25 : 0, now, 0.1);
    this.boost.gain.gain.setTargetAtTime(running && car.boosting ? 0.17 : 0, now, 0.03);
    this.boost.filter.frequency.setTargetAtTime(aircraft ? 1400 : 700, now, 0.1);
    this.skid.gain.gain.setTargetAtTime(
      running && !aircraft && car.grounded ? Math.max(0, Math.abs(input.steer) * speed - 25) * 0.004 : 0, now, 0.05);
    if (state.impact > 0.12 && state.impact > this.lastImpact + 0.06) this.impact(state.impact);
    this.lastImpact = state.impact;
    const countdown = state.phase === 'countdown' ? Math.ceil(state.countdown) : state.phase === 'racing' ? 0 : -1;
    if (countdown !== this.lastCountdown && countdown >= 0) this.tone(countdown ? 660 : 1100, countdown ? 0.12 : 0.3);
    this.lastCountdown = countdown;
    if (gear !== this.lastGear && running && !aircraft) this.impact(0.025);
    if (running && aircraft?.stalled && !this.lastStall) this.tone(420, 0.25);
    this.lastStall = Boolean(aircraft?.stalled);
    this.lastGear = gear;
  }

  silence() {
    if (!this.context) return;
    const now = this.context.currentTime;
    for (const gain of [this.engineGain, this.wind.gain, this.boost.gain, this.skid.gain]) {
      gain.gain.setTargetAtTime(0, now, 0.02);
    }
  }

  level() {
    if (!this.analyser) return 0;
    const data = new Uint8Array(this.analyser.fftSize);
    this.analyser.getByteTimeDomainData(data);
    return Math.sqrt(data.reduce((sum, value) => sum + ((value - 128) / 128) ** 2, 0) / data.length);
  }

  captureTrack() {
    if (!this.context) throw new Error('Start audio before recording gameplay.');
    if (!this.capture) {
      this.capture = this.context.createMediaStreamDestination();
      this.analyser.connect(this.capture);
    }
    return this.capture.stream.getAudioTracks()[0].clone();
  }

  async dispose() {
    if (!this.context) return;
    this.silence();
    for (const source of [...this.oscillators, this.wind.source, this.boost.source, this.skid.source]) source.stop();
    await this.context.close();
  }
}
