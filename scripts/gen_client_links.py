#!/usr/bin/env python3
"""Retired production migration helper.

Modern qeli profiles are INI and the panel/CLI generate qeli:// links directly. Keeping this
historical JSON-to-link script executable risks connecting to production with obsolete schema.
"""
raise SystemExit("RETIRED: generate qeli:// links with the current panel or qeli CLI.")
