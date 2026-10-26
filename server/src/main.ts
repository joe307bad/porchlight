import { createServer } from 'node:http';
import { createHandler } from './app.ts';
import { loadConfig } from './config.ts';

const config = loadConfig();
const handler = createHandler(config);
createServer((req, res) => void handler(req, res)).listen(config.port, () => {
  console.log(`porchlight auth on :${config.port} for ${config.appUrl} (issuer ${config.issuer})`);
});
