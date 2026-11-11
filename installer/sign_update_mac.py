#!/usr/bin/env python3
"""Sign a Coworker Windows installer for the auto-updater, on the release Mac, and write its feed entry.

The Mac half of create_installer.ps1 (its steps 7 to 9), for an installer that was built somewhere without the
private key: by installer/public_build.sh on GitHub's runners. The key never leaves this Mac.

  installer/sign_update_mac.py installer/release/Coworker-1.4.0-win-x64.exe \
      --notes release-notes.html --base-url https://github.com/mmkontis/coworker-windows/releases/download/v1.4.0 \
      --update-appcast

The key is the one Sparkle keeps in the login Keychain (generate_keys --account coworker), the same key the Mac
app's updates are signed with; --key-file takes its exported seed instead. Its public half must be UpdatePublicKey
in Coworker.csproj, or installed apps would refuse the update.

Writes installer/release/appcast-item-windows.xml and <installer>.sha256. With --update-appcast it also puts the
entry at the top of Humanlike-next/public/coworker/appcast-windows.xml, copies the .sha256 beside it and refreshes
latest.json (scripts/update-coworker-downloads.py). Nothing is uploaded or deployed.
"""
import argparse
import base64
import datetime
import hashlib
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

SPARKLE = 'http://www.andymatuschak.org/xml-namespaces/sparkle'
APP_DIR = Path(__file__).resolve().parent.parent
MINIMUM_WINDOWS = '10.0.17763'


def stop(message):
    raise SystemExit(message)


def csproj_value(name):
    match = re.search(rf'<{name}>([^<]*)</{name}>', (APP_DIR / 'Coworker.csproj').read_text(encoding='utf-8'))
    return match.group(1).strip() if match else ''


def sparkle_bin():
    """Sparkle's command line tools, from any Mac build's package checkout."""
    roots = [APP_DIR.parent / 'coworker/build/DerivedData', Path.home() / 'Projects/HumanlikeAI/Humanlike-apps/coworker/build/DerivedData']
    for root in roots:
        tools = root / 'SourcePackages/artifacts/sparkle/Sparkle/bin'
        if (tools / 'sign_update').exists():
            return tools
    return None


def sign(installer, key_file, account):
    """(signature, public key), both base64."""
    tools = sparkle_bin()
    if key_file:
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
        seed = base64.b64decode(Path(key_file).read_text().strip())
        if len(seed) != 32:
            stop(f'{key_file} must hold the base64 of a 32-byte seed.')
        key = Ed25519PrivateKey.from_private_bytes(seed)
        public = key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
        return base64.b64encode(key.sign(installer.read_bytes())).decode(), base64.b64encode(public).decode()
    if not tools:
        stop('Sparkle\'s sign_update was not found (build the Mac app once), and no --key-file was given.')
    public = subprocess.check_output([tools / 'generate_keys', '--account', account, '-p'], text=True).strip().splitlines()[-1]
    signature = subprocess.check_output([tools / 'sign_update', '--account', account, '-p', installer], text=True).strip()
    subprocess.check_call([tools / 'sign_update', '--account', account, '--verify', installer, signature],
                          stdout=subprocess.DEVNULL)
    return signature, public


def verify(installer, signature, public):
    """A second check that does not use the tool that signed."""
    try:
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey
    except ImportError:
        return
    Ed25519PublicKey.from_public_bytes(base64.b64decode(public)).verify(base64.b64decode(signature), installer.read_bytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('installer', type=Path)
    parser.add_argument('--notes', type=Path, required=True, help='HTML release notes')
    parser.add_argument('--base-url', default='https://humanlike.co/coworker/downloads')
    parser.add_argument('--arch', default='x64', choices=['x64', 'arm64'])
    parser.add_argument('--critical', action='store_true')
    parser.add_argument('--update-appcast', action='store_true')
    parser.add_argument('--key-file', help='exported Sparkle private key (base64 seed) instead of the Keychain')
    parser.add_argument('--account', default='coworker', help='Sparkle Keychain account')
    parser.add_argument('--web-public-dir', type=Path, default=APP_DIR.parent.parent / 'Humanlike-next/public/coworker')
    args = parser.parse_args()

    installer = args.installer.resolve()
    version, build = csproj_value('AppVersion'), csproj_value('AppBuild')
    expected_name = f'Coworker-{version}-win-{args.arch}.exe'
    if installer.name != expected_name:
        stop(f'{installer.name} is not {expected_name}, the version in Coworker.csproj.')
    notes = args.notes.read_text(encoding='utf-8').strip()
    if not notes:
        stop('The release notes are empty.')
    appcast = args.web_public_dir / 'appcast-windows.xml'
    if not appcast.exists():
        stop(f'Expected the Windows feed at {appcast}.')
    ET.register_namespace('sparkle', SPARKLE)
    ET.register_namespace('dc', 'http://purl.org/dc/elements/1.1/')
    published = [int(e.get(f'{{{SPARKLE}}}version') or 0) for e in ET.parse(appcast).iter('enclosure')]
    if published and int(build) <= max(published):
        stop(f'Build {build} is not higher than build {max(published)} already in the feed. Raise AppBuild.')

    signature, public = sign(installer, args.key_file, args.account)
    verify(installer, signature, public)
    embedded = csproj_value('UpdatePublicKey')
    if embedded != public:
        stop(f'Coworker.csproj has UpdatePublicKey "{embedded}", the signing key\'s public key is "{public}". '
             'Installed apps would refuse this update.')

    data = installer.read_bytes()
    sha256 = hashlib.sha256(data).hexdigest()
    Path(f'{installer}.sha256').write_text(f'{sha256} *{installer.name}\n')
    url = f"{args.base_url.rstrip('/')}/{installer.name}"
    published_at = datetime.datetime.now(datetime.timezone.utc).strftime('%a, %d %b %Y %H:%M:%S +0000')
    critical = '\n  <sparkle:criticalUpdate />' if args.critical else ''
    item = f'''<item>
  <title>Coworker {version}</title>
  <description><![CDATA[
{notes.replace(']]>', ']]]]><![CDATA[>')}
  ]]></description>
  <pubDate>{published_at}</pubDate>{critical}
  <enclosure
    url="{url}"
    sparkle:os="windows-{args.arch}"
    sparkle:version="{build}"
    sparkle:shortVersionString="{version}"
    sparkle:edSignature="{signature}"
    length="{len(data)}"
    type="application/octet-stream" />
  <sparkle:minimumSystemVersion>{MINIMUM_WINDOWS}</sparkle:minimumSystemVersion>
</item>
'''
    ET.fromstring(f'<rss xmlns:sparkle="{SPARKLE}"><channel>{item}</channel></rss>')
    item_path = installer.parent / 'appcast-item-windows.xml'
    item_path.write_text(item, encoding='utf-8')
    print(f'Signed {installer.name}: {len(data) / 1048576:.1f} MB, sha256 {sha256}')
    print(f'Entry: {item_path}')

    if args.update_appcast:
        text = appcast.read_text(encoding='utf-8')
        indented = '\n'.join(f'    {line}' if line else line for line in item.rstrip('\n').split('\n'))
        first = text.find('<item>')
        if first >= 0:
            start = text.rfind('\n', 0, first) + 1
            text = text[:start] + indented + '\n\n' + text[start:]
        else:
            closing = text.find('</channel>')
            if closing < 0:
                stop('appcast-windows.xml has no </channel>.')
            start = text.rfind('\n', 0, closing) + 1
            text = text[:start] + '\n' + indented + '\n' + text[start:]
        ET.fromstring(text.encode('utf-8'))
        appcast.write_text(text, encoding='utf-8')
        downloads = args.web_public_dir / 'downloads'
        downloads.mkdir(exist_ok=True)
        shutil.copyfile(f'{installer}.sha256', downloads / f'{installer.name}.sha256')
        if url.startswith('https://humanlike.co/coworker/downloads/'):
            shutil.copyfile(installer, downloads / installer.name)
        script = args.web_public_dir.parent.parent / 'scripts/update-coworker-downloads.py'
        subprocess.check_call([sys.executable, script, '--public-dir', args.web_public_dir])
        print(f'Updated {appcast} and latest.json. Commit them together, then deploy the website.')
    print(f'The installer has to be reachable at {url} before the feed is deployed.')


if __name__ == '__main__':
    main()
