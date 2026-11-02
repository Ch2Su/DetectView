# 제3자 고지

DetectView 는 [GNU AGPL-3.0](LICENSE) 으로 공개합니다. 이 저장소에 들어 있는 다른 사람의 저작물은 아래 셋이고, 각각 원래 라이선스를 따릅니다.

| 들어 있는 것 | 원 저작물 | 라이선스 |
|---|---|---|
| `best.onnx` · `best.npu.onnx` | Ultralytics YOLO26s 를 파인튜닝한 모델과 그 NPU 양자화본 | AGPL-3.0 |
| `WindowsMlExecutionProvider.cs` | YoloDotNet 의 DirectML 실행 공급자 | MIT |
| `.claude/helpers/graft-hooks.cjs` · `.claude/helpers/graft-statusline.cjs` · `.claude/skills/graft/SKILL.md` | graft(코드 인덱스 도구)가 만든 Claude Code 연동 파일 | MIT |

빌드할 때 NuGet 이 내려받는 패키지(OpenCvSharp4 · SkiaSharp · Vortice · DirectShowLib.Standard · Windows ML 등)는 저장소에 들어 있지 않습니다.
목록과 라이선스는 [README 의 라이선스 절](README.md#라이선스)에 있습니다. 빌드한 실행 파일을 배포할 땐 그 패키지들의 고지도 함께 넣어야 합니다.

## Ultralytics YOLO26 — AGPL-3.0

`best.onnx` 는 [Ultralytics](https://github.com/ultralytics/ultralytics) YOLO26s 를 7클래스로 파인튜닝해 ONNX 로 내보낸 모델이고,
`best.npu.onnx` 는 그것을 `tools/npu_quantize.py` 로 양자화한 것입니다. 두 파일의 메타데이터에도
`license = AGPL-3.0 License (https://ultralytics.com/license)` 가 들어 있습니다.

Ultralytics 는 YOLO 로 학습한 모델과 그 모델을 쓰는 프로젝트에 AGPL-3.0 을 요구합니다([Ultralytics Licensing](https://www.ultralytics.com/license)).
그래서 이 저장소 전체를 AGPL-3.0 으로 공개합니다. 라이선스 전문은 [LICENSE](LICENSE) 에 있습니다.

## YoloDotNet — MIT

`WindowsMlExecutionProvider.cs` 는 [YoloDotNet](https://github.com/NickSwardh/YoloDotNet) 의 `YoloDotNet.ExecutionProvider.DirectML` 1.0.0 에 들어 있는
`DirectMLExecutionProvider` 를 바탕으로 했습니다(세션에 실행 공급자를 붙이는 부분을 Windows ML 의 EP 장치 API 로 바꿈). 이 파일은 MIT 입니다.

```text
MIT License

Copyright (c) Niklas Swärd

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## graft — MIT

`.claude/helpers/graft-hooks.cjs` · `.claude/helpers/graft-statusline.cjs` · `.claude/skills/graft/SKILL.md` 는
[graft](https://github.com/NanoNets/context-graph-engine)(`@nanonets/graft` 0.18.0)가 이 저장소에 만들어 넣은 Claude Code 연동 파일입니다.
두 `.cjs` 파일은 graft 설치 경로를 찾는 줄 하나만 고쳤습니다(설치 때 박힌 사용자 폴더 경로 대신 `%APPDATA%` 로 계산).

```text
MIT License

Copyright (c) 2026 Context Graph Engine contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
