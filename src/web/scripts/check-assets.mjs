// `check:assets` — сборка обязана совпадать с закоммиченными `src/DshPanel/WebAssets`.
//
// Зачем: WebAssets коммитится (калитка вшивает его в один exe). Если кто-то правил исходники, но
// забыл пересобрать, в продукт уехал бы старый пульт. Поэтому: собираем во временную папку и
// сравниваем файлы и их SHA-256; расхождение — красный прогон с именами разошедшихся файлов.

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, relative, resolve } from "node:path";

const HERE = resolve(import.meta.dirname, "..");
const COMMITTED = resolve(HERE, "../DshPanel/WebAssets");
const scratch = mkdtempSync(join(tmpdir(), "dsh-web-assets-"));

function files(root) {
  const out = [];
  const walk = (dir) => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      if (statSync(full).isDirectory()) walk(full);
      else out.push(relative(root, full).replaceAll("\\", "/"));
    }
  };
  walk(root);
  return out.sort();
}

function hashes(root, list) {
  const map = new Map();
  for (const name of list) {
    map.set(
      name,
      createHash("sha256")
        .update(readFileSync(join(root, name)))
        .digest("hex"),
    );
  }
  return map;
}

try {
  // Vite запускается своим bin-файлом через текущий Node: без npx и без shell —
  // иначе на Windows код возврата теряется и «сборка не прошла» врёт.
  const viteBin = join(HERE, "node_modules/vite/bin/vite.js");
  const built = spawnSync(
    process.execPath,
    [viteBin, "build", "--outDir", scratch, "--emptyOutDir"],
    { cwd: HERE, stdio: "inherit" },
  );
  if (built.status !== 0) {
    console.error(`check:assets — сборка не прошла (код ${built.status ?? "?"})`);
    process.exit(1);
  }

  const fresh = files(scratch);
  const committed = files(COMMITTED);
  const missing = fresh.filter((name) => !committed.includes(name));
  const extra = committed.filter((name) => !fresh.includes(name));
  const freshHashes = hashes(scratch, fresh);
  const committedHashes = hashes(COMMITTED, committed);
  const changed = fresh.filter(
    (name) => committed.includes(name) && freshHashes.get(name) !== committedHashes.get(name),
  );

  if (missing.length + extra.length + changed.length > 0) {
    console.error("check:assets — сборка разошлась с src/DshPanel/WebAssets:");
    if (missing.length > 0) console.error(`  нет в дереве: ${missing.join(", ")}`);
    if (extra.length > 0) console.error(`  лишнее в дереве: ${extra.join(", ")}`);
    if (changed.length > 0) console.error(`  другой хэш: ${changed.join(", ")}`);
    console.error("Пересоберите: npm run build и закоммитьте src/DshPanel/WebAssets.");
    process.exit(1);
  }

  console.log(`check:assets — ${fresh.length} файлов совпадают побайтово`);
} finally {
  rmSync(scratch, { recursive: true, force: true });
}
