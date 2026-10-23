// Serves the Unity WebGL build (Builds/WebGL, made by PixelHero.Editor.WebGLBuilder.BuildBatch) for checking in a browser.
// Gzipped build files (*.gz) are sent with Content-Encoding: gzip and the type of the file inside, which is what the
// Unity loader expects (the build also has the decompression fallback, so a plain server works, only slower).
//
//   node Tools/serve_webgl.mjs [port]          (default 9050; the game server must run on :59011)
//   then open http://localhost:9050/            (?server=ws://host:port picks another game server)
//   node Tools/serve_webgl.mjs 9051 --https [certificate.pfx | fullchain.pem [privkey.pem]]
//                                               serves over https with the PFX (default Server/certs/localhost.pfx,
//                                               no password) or a PEM chain and key (Let's Encrypt; the key defaults
//                                               to privkey.pem next to the chain); the page then connects to
//                                               wss://<page host>:59443, the C# server's secure listener (SSL_PORT)
import fs from 'node:fs';
import http from 'node:http';
import https from 'node:https';
import path from 'node:path';

const root = path.resolve('Builds/WebGL');
const cli = process.argv.slice(2);
const httpsAt = cli.indexOf('--https');
const certificateArg = cli[httpsAt + 1] ?? '';
const certificate =
    httpsAt < 0 ? null : path.resolve(/\.(pfx|p12|pem)$/.test(certificateArg) ? certificateArg : 'Server/certs/localhost.pfx');
const pem = certificate?.endsWith('.pem') ?? false;
const privateKey = pem
    ? path.resolve(cli[httpsAt + 2]?.endsWith('.pem') ? cli[httpsAt + 2] : path.join(path.dirname(certificate), 'privkey.pem'))
    : null;
const port = Number(cli.find((arg) => /^\d+$/.test(arg)) ?? 9050);
const scheme = certificate ? 'https' : 'http';
const types = {
    '.html': 'text/html; charset=utf-8',
    '.js': 'application/javascript',
    '.wasm': 'application/wasm',
    '.data': 'application/octet-stream',
    '.json': 'application/json',
    '.css': 'text/css',
    '.png': 'image/png',
    '.ico': 'image/x-icon'
};

if (!fs.existsSync(path.join(root, 'index.html'))) {
    console.error(`${root}/index.html is missing: build first (PixelHero.Editor.WebGLBuilder.BuildBatch).`);
    process.exit(1);
}

for (let file of [certificate, privateKey])
    if (file && !fs.existsSync(file)) {
        console.error(`${file} is missing (see PROGRESS: wss).`);
        process.exit(1);
    }

let tls = !certificate
    ? null
    : pem
      ? { cert: fs.readFileSync(certificate), key: fs.readFileSync(privateKey) }
      : { pfx: fs.readFileSync(certificate), passphrase: '' };

let serve = (request, response) => {
    let url = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
    if (url.endsWith('/')) url += 'index.html';
    let file = path.join(root, url);
    if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
        response.writeHead(404);
        response.end('not found');
        return;
    }

    let headers = { 'Cache-Control': 'no-cache' };
    let name = file;
    if (name.endsWith('.gz')) {
        headers['Content-Encoding'] = 'gzip';
        name = name.slice(0, -3);
    }
    headers['Content-Type'] = types[path.extname(name)] ?? 'application/octet-stream';
    response.writeHead(200, headers);
    fs.createReadStream(file).pipe(response);
};

(tls ? https.createServer(tls, serve) : http.createServer(serve)).listen(port, () =>
    console.log(`Serving ${root} at ${scheme}://localhost:${port}/`)
);
