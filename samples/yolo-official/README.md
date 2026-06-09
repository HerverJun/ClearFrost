# Official YOLO ONNX Samples

This folder is used for local acceptance samples generated from Ultralytics official YOLO11 nano models.

Model binaries are ignored by the repository root `.gitignore`:

- `*.pt`
- `*.onnx`

Generate samples:

```powershell
python tools/yolo_samples/export_official_yolo.py
```

Run contract acceptance and a small CPU benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/yolo_samples/accept_official_yolo.ps1
```

The acceptance reports are written under `samples/yolo-official/reports/`.
