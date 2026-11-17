# Updates

The Windows, macOS and Linux apps update themselves from this repository's
releases, like this:

1. **On start** (and with **Check for updates** under the version in the
   sidebar), the app downloads `update.json` and `update.json.sig` from the
   latest release.
2. **It checks the signature first.** `update.json.sig` is an ECDSA P-256
   signature (SHA-256, raw r‖s, base64) over `update.json`, made by
   [`release.yml`](../.github/workflows/release.yml) with a private key that
   only this repository's `UPDATE_SIGNING_KEY` secret holds. The app checks it
   against the public key it was built with ([`public-key.pem`](public-key.pem)):
   Windows with .NET's `ECDsa`, macOS with CryptoKit, Linux with the `openssl`
   command. A manifest that doesn't check out is ignored.
3. **When a newer version is listed**, the sidebar offers it. Installing asks
   for a second click (unsaved edits are lost), downloads this platform's file,
   checks its size and SHA-256 against the signed manifest, and only then puts
   it in place and restarts:
   - Windows: the new `OOT SW2SE.exe` from the zip replaces the running one
     (which is kept as `OOT SW2SE.exe.old` until the next start).
   - macOS: the new `OOT SW2SE.app` replaces the running one.
   - Linux: the AppImage or `.pyz` file is replaced atomically. A checkout
     started with `python3 linux/oot_sw2se.py` doesn't update itself.
4. If anything fails, nothing is replaced, and the sidebar says why.

The check sends one request to GitHub at start; nothing about you or your
saves is sent. Set `OOT_SW2SE_NO_UPDATE_CHECK=1` to only check when you click.
The web version has nothing to update: serve the newest image or page.

Without `public-key.pem` in the build, updates are off and the version is
shown as a plain label.

## One-time setup: the signing key

The private key never goes into Git. Run this once, in a checkout, on a
computer you trust:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out update-signing-key.pem
openssl ec -in update-signing-key.pem -pubout -out update/public-key.pem
gh secret set UPDATE_SIGNING_KEY --repo SkippySteve/OOT-SW2SE < update-signing-key.pem
git add update/public-key.pem && git commit -m "Updates: public key"
```

Then keep `update-signing-key.pem` somewhere safe and offline (a password
manager or an encrypted drive) and delete the copy in the checkout. GitHub
doesn't show a secret again, so that copy is the only backup.

If the key is ever lost or leaks: make a new pair the same way, and publish
one more release signed with the **old** key whose apps carry the **new**
public key. Apps that missed it need a manual download once.

## Making a release

1. Set the version in `Program.cs` (`AssemblyVersion` and
   `AssemblyFileVersion`), commit, and merge.
2. Tag the merged commit and push the tag: `git tag v2.0.1 && git push origin v2.0.1`.
3. [`release.yml`](../.github/workflows/release.yml) checks the tag against
   `Program.cs`, builds Windows, macOS, Linux and the web page from that
   commit, writes and signs `update.json`
   ([`sign_manifest.py`](sign_manifest.py)), checks the signature with
   `public-key.pem`, and publishes the release with every file.
   [`web.yml`](../.github/workflows/web.yml) pushes the container image for the
   same tag.

Don't replace release files afterwards: the apps would refuse them, since
their checksums are signed.
