"""Offline regression checks for the download and extraction boundary."""
import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / 'src/KaraokeFilmMaker.Infrastructure/Alignment/Runtime/install-alignment.py'
spec = importlib.util.spec_from_file_location('alignment_installer', SCRIPT)
installer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(installer)

class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        installer.CACHE = self.root / 'cache'
        installer.CACHE.mkdir()
        self.data = b'verified model fixture'
        self.digest = hashlib.sha256(self.data).hexdigest()
        self.dest = self.root / 'model.bin'

    def tearDown(self):
        self.temp.cleanup()

    def test_valid_cache_is_checked_and_reused_without_network(self):
        (installer.CACHE / self.digest).write_bytes(self.data)
        with patch.object(installer.urllib.request, 'urlopen', side_effect=AssertionError('network forbidden')):
            installer.download('https://unused', self.dest, self.digest, len(self.data))
        self.assertEqual(self.data, self.dest.read_bytes())

    def test_corrupt_cache_is_replaced_only_by_verified_download(self):
        (installer.CACHE / self.digest).write_bytes(b'x' * len(self.data))
        with patch.object(installer.urllib.request, 'urlopen', return_value=io.BytesIO(self.data)):
            installer.download('https://fixture', self.dest, self.digest, len(self.data))
        self.assertEqual(self.data, self.dest.read_bytes())
        self.assertEqual(self.data, (installer.CACHE / self.digest).read_bytes())

    def test_corrupt_download_never_becomes_installed_file(self):
        self.dest.write_bytes(b'previous content')
        with patch.object(installer.urllib.request, 'urlopen', side_effect=lambda *a, **kw: io.BytesIO(b'bad')) as request:
            with self.assertRaisesRegex(RuntimeError, '校验失败'):
                installer.download('https://fixture', self.dest, self.digest, len(self.data))
        self.assertEqual(3, request.call_count)
        self.assertEqual(b'previous content', self.dest.read_bytes())
        self.assertFalse((installer.CACHE / self.digest).exists())

    def test_archive_cannot_escape_runtime(self):
        archive=self.root/'bad.zip'
        with zipfile.ZipFile(archive,'w') as z: z.writestr('../escaped.txt','bad')
        with self.assertRaisesRegex(RuntimeError, '路径无效'):
            installer.extract(archive,self.root/'runtime')
        self.assertFalse((self.root/'escaped.txt').exists())

if __name__ == '__main__': unittest.main()
