// stub-dsh.js — заглушка вместо настоящего dsh: проверка того, что приложение
// правильно поднимает сервер, ловит ссылку для входа из его вывода, пишет
// журнал и корректно гасит процесс. Настоящий сервер при этом не трогается.
//
// Запускается ровно так же, как настоящий:
//   node stub-dsh.js web --no-open --port 3099

import http from "node:http";

let port = 3080;
const argv = process.argv.slice(2);
for (let index = 0; index < argv.length; index += 1) {
  if (argv[index] === "--port" && argv[index + 1]) port = Number(argv[index + 1]);
}

const server = http.createServer((request, response) => {
  // Правильный токен — только свой: чужой (устаревший) адрес получает 401,
  // как у настоящего сервера.
  const ok = (request.url ?? "").includes(`token=stub-token-${port}`);
  response.writeHead(ok ? 200 : 401, { "Content-Type": "text/plain; charset=utf-8" });
  response.end(ok ? "stub\n" : "unauthorized\n");
});

// STUB_DELAY_MS — задержка перед началом прослушивания: так проверяется, что
// приложение не открывает браузер раньше, чем страница начинает отвечать.
const delayMs = Number(process.env.STUB_DELAY_MS ?? "0");

console.log(`stub: слушаю порт ${port} (PID ${process.pid})`);
console.log(`dsh web: http://127.0.0.1:${port}/?token=stub-token-${port}`);

setTimeout(() => {
  server.listen(port, "127.0.0.1", () => {
    console.log(`stub: прослушивание начато (задержка ${delayMs} мс)`);
  });
}, delayMs);

process.on("SIGTERM", () => process.exit(0));
