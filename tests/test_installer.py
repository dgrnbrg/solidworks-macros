"""Tests for Install-Macro.bat and scripts/Install-Macro.ps1."""

from __future__ import annotations

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent


class TestMacroInstaller(unittest.TestCase):
    def test_installer_files_exist(self) -> None:
        self.assertTrue((REPO_ROOT / "Install-Macro.bat").is_file(), "Install-Macro.bat missing")
        self.assertTrue((REPO_ROOT / "Install-Macro.cmd").is_file(), "Install-Macro.cmd missing")
        self.assertTrue((REPO_ROOT / "scripts" / "Install-Macro.ps1").is_file(), "Install-Macro.ps1 missing")

    def test_install_macro_ps1_builds_valid_swp(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            out_swp = Path(td) / "TestInstalled.swp"
            cmd = [
                "powershell",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(REPO_ROOT / "scripts" / "Install-Macro.ps1"),
                "-OutputSwp",
                str(out_swp),
                "-NonInteractive",
            ]
            res = subprocess.run(cmd, capture_output=True, text=True, cwd=str(REPO_ROOT))
            self.assertEqual(res.returncode, 0, f"Install-Macro.ps1 failed: {res.stdout}\n{res.stderr}")
            self.assertTrue(out_swp.is_file(), "Target .swp file was not created")
            self.assertGreater(out_swp.stat().st_size, 50_000, "Created .swp file is unexpectedly small")


if __name__ == "__main__":
    unittest.main()
