// Заглушка только для jsdom: Semi тянет lottie-web, а у jsdom нет canvas.
export default {
  loadAnimation: () => ({ destroy() {}, play() {}, stop() {} }),
  setQuality: () => undefined,
};
