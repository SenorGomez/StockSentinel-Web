"""Exercise the real installer in a temporary tree; privileged tools/services are fakes."""
import io
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="stocksentinel-deploy-test-")
        self.root = Path(self.temp.name)
        self.app = self.root / "app"
        self.app.mkdir()
        (self.app / "data").mkdir()
        self.data = {"sentinel.db": b"existing database", "push-vapid.json": b"existing private key"}
        for name, data in self.data.items():
            (self.app / "data" / name).write_bytes(data)
        self.config = b'{"Sentinel":{"DatabasePath":"data/sentinel.db"},"custom":"keep"}'
        (self.app / "appsettings.json").write_bytes(self.config)
        self.envfile = self.root / "environment"
        self.envfile.write_text("STOCKSENTINEL_API_KEY=existing-test-key\nPush__VapidSubject=mailto:test@example.com\n")
        self.original_env = self.envfile.read_bytes()
        self.caddy = self.root / "Caddyfile"
        self.caddy.write_text("# existing unrelated site\nexample.test {\n respond ok\n}\n")
        self.original_caddy = self.caddy.read_bytes()
        self.service = self.root / "service"
        self.service.write_text("legacy service\n")
        self.original_service = self.service.read_bytes()
        (self.app / "StockSentinel.Web").write_bytes(b"legacy executable")
        (self.app / "wwwroot").mkdir()
        (self.app / "wwwroot" / "index.html").write_bytes(b"legacy asset")
        self.archive = self.root / "release.tgz"
        with tarfile.open(self.archive, "w:gz") as archive:
            for name, data in {"StockSentinel.Web": b"new executable", "wwwroot/index.html": b"new asset",
                               "appsettings.json": b'{"bootstrap":true}', "data/push-vapid.json": b"must never replace live data"}.items():
                entry = tarfile.TarInfo("./" + name)
                entry.size = len(data)
                entry.mode = 0o755 if name == "StockSentinel.Web" else 0o644
                archive.addfile(entry, io.BytesIO(data))
        script = (ROOT / "deploy/install-pi.sh").read_text()
        for name, value in {"APP": self.app, "ENVFILE": self.envfile, "CADDY": self.caddy, "SERVICE": self.service}.items():
            old = next(line for line in script.splitlines() if line.startswith(name + "="))
            script = script.replace(old, name + "='" + str(value) + "'")
        self.installer = self.root / "install-pi.sh"
        self.installer.write_text(script)
        (self.root / "stocksentinel.service").write_text((ROOT / "deploy/stocksentinel.service").read_text())
        self.bin = self.root / "bin"
        self.bin.mkdir()
        # macOS lacks GNU mv -T. Emulate its atomic replacement for this test only.
        self.fake("mv", """import os,sys,subprocess
if '-Tf' in sys.argv: os.replace(sys.argv[-2],sys.argv[-1])
else: sys.exit(subprocess.call(['/bin/mv']+sys.argv[1:]))
""")
        self.fake("systemctl", """import os,sys,pathlib
root=pathlib.Path(os.environ['DEPLOY_TEST_ROOT']);args=sys.argv[1:]
with (root/'commands').open('a') as f:f.write(' '.join(args)+'\\n')
if args==['is-active','--quiet','stocksentinel'] and not (root/'service').exists():sys.exit(3)
if args==['restart','stocksentinel']:
 marker=root/'restarted'
 if os.environ.get('FAIL_RESTART') and not marker.exists():marker.touch();sys.exit(1)
if args==['reload','caddy'] and os.environ.get('FAIL_RELOAD'):
 marker=root/'reloaded'
 if not marker.exists():marker.touch();sys.exit(1)
""")
        self.fake("caddy", "import os,sys\nsys.exit(1 if os.environ.get('FAIL_VALIDATE') else 0)\n")
        self.fake("curl", "import os,sys\nsys.exit(1 if os.environ.get('FAIL_HEALTH') else 0)\n")
        for command in ("id", "chown", "sleep"):
            self.fake(command, "")
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                DEPLOY_TEST_ROOT=str(self.root))

    def fake(self, name, code):
        path = self.bin / name
        path.write_text("#!/usr/bin/env python3\n" + code)
        path.chmod(0o755)

    def run_install(self, **failures):
        return subprocess.run(["bash", str(self.installer), str(self.archive)], env=dict(self.environment, **failures),
                              capture_output=True, text=True, timeout=30)

    def assert_persistent_state(self):
        for name, data in self.data.items():
            self.assertEqual((self.app / "data" / name).read_bytes(), data)
        self.assertEqual((self.app / "appsettings.json").read_bytes(), self.config)
        self.assertEqual(self.envfile.read_bytes(), self.original_env)
        self.assertEqual((self.app / "StockSentinel.Web").read_bytes(), b"legacy executable")
        self.assertEqual((self.app / "wwwroot/index.html").read_bytes(), b"legacy asset")

    def test_stages_and_atomically_activates_without_overwriting_state(self):
        result = self.run_install()
        self.assertEqual(result.returncode, 0, result.stderr)
        current = self.app / "current"
        self.assertTrue(current.is_symlink())
        self.assertEqual((current / "StockSentinel.Web").read_bytes(), b"new executable")
        self.assertEqual((current / "wwwroot/index.html").read_bytes(), b"new asset")
        self.assertTrue((current / "appsettings.json").is_symlink())
        self.assertFalse((current / "data").exists())
        self.assertEqual(self.service.read_bytes(), (self.root / "stocksentinel.service").read_bytes())
        self.assert_persistent_state()
        previous = current.resolve()
        result = self.run_install()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotEqual(current.resolve(), previous)
        self.assertTrue(previous.exists())
        self.assert_persistent_state()

    def test_restart_health_and_reload_failures_restore_legacy_service(self):
        for failure in ("FAIL_RESTART", "FAIL_HEALTH", "FAIL_RELOAD"):
            with self.subTest(failure=failure):
                result = self.run_install(**{failure: "1"})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("restored the previous release", result.stderr)
                self.assertFalse((self.app / "current").exists())
                self.assertEqual(self.service.read_bytes(), self.original_service)
                self.assertEqual(self.caddy.read_bytes(), self.original_caddy)
                self.assert_persistent_state()

    def test_failed_upgrade_restores_previous_symlink(self):
        self.assertEqual(self.run_install().returncode, 0)
        previous = (self.app / "current").resolve()
        service = self.service.read_bytes()
        caddy = self.caddy.read_bytes()
        result = self.run_install(FAIL_HEALTH="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual((self.app / "current").resolve(), previous)
        self.assertEqual(self.service.read_bytes(), service)
        self.assertEqual(self.caddy.read_bytes(), caddy)
        self.assert_persistent_state()

    def test_validation_failure_does_not_activate(self):
        result = self.run_install(FAIL_VALIDATE="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("before activation", result.stderr)
        self.assertFalse((self.app / "current").exists())
        self.assertEqual(self.service.read_bytes(), self.original_service)
        self.assert_persistent_state()

    def test_first_install_failure_removes_new_service_but_keeps_bootstrap_config(self):
        self.service.unlink()
        (self.app / "appsettings.json").unlink()
        result = self.run_install(FAIL_HEALTH="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(self.service.exists())
        self.assertFalse((self.app / "current").exists())
        self.assertEqual((self.app / "appsettings.json").read_bytes(), b'{"bootstrap":true}')
        for name, data in self.data.items():
            self.assertEqual((self.app / "data" / name).read_bytes(), data)
        self.assertEqual(self.envfile.read_bytes(), self.original_env)

    def tearDown(self):
        self.temp.cleanup()


if __name__ == "__main__":
    unittest.main()
