import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const apiBaseUrl = ({{apiEnvKeysExpression}})
  .trim()
  .replace(/\/$/, '');

if (!apiBaseUrl && (process.env.VERCEL === '1' || process.env.CI === 'true')) {
  console.error('Missing {{apiEnvKeysList}} for production build. API requests will hit the Vercel SPA and fail with 405.');
  process.exit(1);
}

const __dirname = dirname(fileURLToPath(import.meta.url));
const target = join(__dirname, '..', 'src', 'environments', 'environment.production.ts');
const content = `export const environment = {\n  production: true,\n  apiBaseUrl: '${apiBaseUrl}',\n  apiUrl: '${apiBaseUrl}/api'\n};\n`;
writeFileSync(target, content, 'utf8');
console.log('[write-api-env] Wrote environment.production.ts (apiBaseUrl=' + (apiBaseUrl || '(empty)') + ')');
