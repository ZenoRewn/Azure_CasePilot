// 麦克风采集: 把每个渲染块 (Float32, 上下文采样率 16kHz) 转发给主线程
registerProcessor("pcm", class extends AudioWorkletProcessor {
  process(inputs) { const c = inputs[0][0]; if (c) this.port.postMessage(c.slice(0)); return true; }
});
