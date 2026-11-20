export async function toggleFullscreen(element, document = globalThis.document) {
  if (document.fullscreenElement === element) {
    await document.exitFullscreen();
    return false;
  }
  if (!element.requestFullscreen || document.fullscreenEnabled === false) {
    throw new Error('Fullscreen is unavailable here. The game still fits the browser window.');
  }
  await element.requestFullscreen();
  return true;
}

function download(blob, name) {
  const url = URL.createObjectURL(blob), link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export class GameplayCapture {
  recorder = null;
  stream = null;
  chunks = [];

  start(canvas, audio, onStopped, onError) {
    if (!canvas.captureStream || !globalThis.MediaRecorder) {
      throw new Error('This browser does not support gameplay recording.');
    }
    const type = ['video/webm;codecs=vp9,opus', 'video/webm;codecs=vp8,opus', 'video/webm']
      .find(candidate => MediaRecorder.isTypeSupported(candidate));
    if (!type) throw new Error('No supported WebM recording format was found.');
    this.stream = canvas.captureStream(60);
    try {
      this.stream.addTrack(audio.captureTrack());
      this.recorder = new MediaRecorder(this.stream, { mimeType: type, videoBitsPerSecond: 7_000_000 });
    } catch (error) {
      for (const track of this.stream.getTracks()) track.stop();
      this.stream = null;
      throw error;
    }
    this.chunks = [];
    this.error = null;
    this.recorder.ondataavailable = event => { if (event.data.size) this.chunks.push(event.data); };
    this.recorder.onerror = event => {
      this.error = event.error ?? new Error('Gameplay recording failed.');
      onError(this.error);
      if (this.recorder.state === 'recording' || this.recorder.state === 'paused') this.recorder.stop();
    };
    this.recorder.onstop = () => {
      const blob = new Blob(this.chunks, { type });
      for (const track of this.stream.getTracks()) track.stop();
      this.stream = null;
      this.recorder = null;
      this.chunks = [];
      const downloaded = blob.size > 0 && !this.error;
      if (downloaded) download(blob, 'poly-vector-racer-gameplay.webm');
      else if (!this.error) onError(new Error('The recording contained no video frames.'));
      onStopped(downloaded);
    };
    try { this.recorder.start(500); }
    catch (error) {
      for (const track of this.stream.getTracks()) track.stop();
      this.stream = null;
      this.recorder = null;
      this.chunks = [];
      throw error;
    }
  }

  stop() {
    if (this.recorder?.state === 'recording') this.recorder.stop();
  }
}
