// Registers the accounts the PlayMode live tests log in to, on the server Tools/live_test_server.sh runs:
// - the admin test account of .test-account.json (created with a random password when the file is missing),
//   given rank 2 in the local MongoDB container "pixelhero";
// - unitytest1, the offline account FriendsInteractAndTrade adds as a friend.
// Accounts that already exist are left as they are, so it can run again.
//
//   node Tools/seed_test_accounts.mjs [ws://127.0.0.1:59011]
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const server = process.argv[2] ?? 'ws://127.0.0.1:59011';
const accountFile = path.join(root, '.test-account.json');
const gver = /^GVER='([^']*)'/m.exec(readFileSync(path.join(root, 'PixelHero-Data', '.env.defaults'), 'utf8'))[1];

// Packets and Opcodes.Login (Scripts/Runtime/Common/Generated).
const Handshake = 1, Login = 2, Welcome = 3, Ready = 9;
const Register = 1;

let admin;
if (existsSync(accountFile)) admin = JSON.parse(readFileSync(accountFile, 'utf8'));
else {
    admin = { username: 'unityadmin', password: randomBytes(12).toString('hex') };
    writeFileSync(accountFile, JSON.stringify(admin, null, 2) + '\n');
    console.log(`Wrote ${accountFile}`);
}

/** Registers one account: Handshake, Login (register), then Ready so the server saves the new character. */
function register(username, password) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(server);
        const timer = setTimeout(() => {
            socket.close();
            reject(new Error(`${username}: no answer from ${server}`));
        }, 30_000);
        const done = result => {
            clearTimeout(timer);
            socket.close();
            resolve(result);
        };

        socket.onerror = () => {
            clearTimeout(timer);
            reject(new Error(`cannot connect to ${server}`));
        };
        // The server registers a connection a moment after accepting it and drops a packet sent before that.
        socket.onopen = () => setTimeout(() => socket.send(JSON.stringify([Handshake, { gVer: gver }])), 500);
        // A rejection is the close reason: "userexists" or "emailexists" for an account made by an earlier run.
        socket.onclose = ({ reason }) => done(reason || 'closed');
        socket.onmessage = ({ data }) => {

            for (const [id] of JSON.parse(data)) {
                if (id === Handshake)
                    socket.send(JSON.stringify([Login, { opcode: Register, username, password, email: `${username}@example.com` }]));
                else if (id === Welcome) {
                    socket.send(JSON.stringify([Ready, { regionsLoaded: 0, userAgent: 'seed_test_accounts' }]));
                    setTimeout(() => done('registered'), 1000);
                }
            }
        };
    });
}

for (const [username, password] of [
    [admin.username, admin.password],
    ['unitytest1', randomBytes(12).toString('hex')]
]) {
    console.log(`${username}: ${await register(username, password)}`);
    // The server refuses a reconnect from the same IP within 5 s unless DEBUGGING is on (the script server has it).
    await new Promise(resolve => setTimeout(resolve, 500));
}

const update = `db.player_info.updateOne({ username: ${JSON.stringify(admin.username.toLowerCase())} }, { $set: { rank: 2 } })`;
const result = execFileSync('docker', ['exec', 'pixelhero', 'mongo', '--quiet', '-u', 'pixelhero', '-p', 'pixelhero',
    '--authenticationDatabase', 'pixelhero', 'pixelhero', '--eval', `printjson(${update})`], { encoding: 'utf8' });
console.log(`${admin.username} rank 2: ${result.trim().replace(/\s+/g, ' ')}`);
