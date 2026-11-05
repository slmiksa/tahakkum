// Connects this copy of the code to a Supabase project:
//   - creates the license signing key (once; it goes in licensing/secrets, outside the app)
//   - writes the public key and API address into src/License.cs
//   - writes the project address into dashboard/config.js
//
//   node tools/setup.mjs <project ref> <anon key> [repository root]
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const [ref, anonKey, rootArg] = process.argv.slice(2);
if (!ref || !anonKey) {
  console.error("usage: node tools/setup.mjs <project ref> <anon key> [repository root]");
  process.exit(2);
}
if (!/^[a-z0-9]+$/.test(ref)) {
  console.error("The project ref is the short id in your project's address, such as abcdefghijklmnop.");
  process.exit(2);
}
if (anonKey.startsWith("sb_secret_") || anonKey.includes("service_role") || isServiceRole(anonKey)) {
  console.error("This looks like the secret key. Use the publishable key: the secret one must never leave Supabase.");
  process.exit(2);
}

function isServiceRole(key) {
  try {
    return JSON.parse(Buffer.from(key.split(".")[1], "base64url").toString()).role === "service_role";
  } catch {
    return false;
  }
}

const backend = join(dirname(fileURLToPath(import.meta.url)), "..");
const root = rootArg ?? join(backend, "..");
const secretsDir = join(rootArg ?? backend, "secrets");
const keyFile = join(secretsDir, "license-signing-key.json");

// Replacing an existing key would invalidate every license already issued, so it is never done here.
let privateJwk;
if (existsSync(keyFile)) {
  privateJwk = JSON.parse(readFileSync(keyFile, "utf8"));
  console.log("Using the existing signing key.");
} else {
  const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  privateJwk = await crypto.subtle.exportKey("jwk", pair.privateKey);
  mkdirSync(secretsDir, { recursive: true });
  writeFileSync(keyFile, JSON.stringify(privateJwk), { mode: 0o600 });
  console.log("Created a new signing key. Back it up somewhere safe: it cannot be recovered.");
}

function setConst(text, name, value) {
  const pattern = new RegExp('(public const string ' + name + ' = )"[^"]*"');
  if (!pattern.test(text)) throw new Error("Could not find " + name + " in client-program/src/License.cs");
  return text.replace(pattern, '$1"' + value + '"');
}

const licenseFile = join(root, "client-program", "src", "License.cs");
let license = readFileSync(licenseFile, "utf8");
license = setConst(license, "DefaultApiUrl", "https://" + ref + ".supabase.co/functions/v1");
license = setConst(license, "DefaultPublicX", privateJwk.x);
license = setConst(license, "DefaultPublicY", privateJwk.y);
writeFileSync(licenseFile, license);

const configFile = join(root, "dashboard", "config.js");
writeFileSync(configFile,
  "// Filled in by supabase-backend/tools/setup.mjs. The anon key is public by design: the database rules are what protect the data.\n" +
  "window.TAHAKKUM_CONFIG = " + JSON.stringify({ url: "https://" + ref + ".supabase.co", anonKey }) + ";\n");

console.log("Done. Public key for the relay server:");
console.log("  x = " + privateJwk.x);
console.log("  y = " + privateJwk.y);
console.log("Rebuild the program with build.ps1 from now on: the build requires activation.");
