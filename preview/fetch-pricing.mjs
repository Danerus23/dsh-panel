// fetch-pricing.mjs — сохраняет официальную страницу цен в файл: страницу
// разбирает приложение, а этот скрипт нужен только чтобы получить её там, где
// у .NET в песочнице нет доступа к TLS (Node идёт через свой OpenSSL).

import { writeFileSync } from "node:fs";
import https from "node:https";

const url = process.argv[2] ?? "https://api-docs.deepseek.com/quick_start/pricing";
const out = process.argv[3] ?? "pricing.html";

function get(url, hops) {
  https.get(url, (response) => {
    if ([301, 302, 303, 307, 308].includes(response.statusCode) && hops > 0 && response.headers.location) {
      response.resume();
      const next = new URL(response.headers.location, url).toString();
      console.log(`редирект ${response.statusCode} → ${next}`);
      get(next, hops - 1);
      return;
    }

    let body = "";
    response.setEncoding("utf8");
    response.on("data", (chunk) => { body += chunk; });
    response.on("end", () => {
      writeFileSync(out, body, "utf8");
      console.log(`HTTP ${response.statusCode}, сохранено в ${out}: ${body.length} символов`);
    });
  }).on("error", (error) => {
    console.error("ошибка: " + error.message);
    process.exit(1);
  });
}

get(url, 5);
