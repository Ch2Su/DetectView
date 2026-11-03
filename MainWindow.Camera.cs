using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Windows;

namespace DetectView
{
    /// <summary>카메라 열거(DirectShow — 인덱스가 OpenCV DSHOW 순서와 일치)와 시작.</summary>
    public partial class MainWindow
    {
        private readonly List<int> _cameraIndices = [];

        // DirectShow로 연결된 영상 입력 장치를 "이름"과 함께 열거한다.
        // 카메라를 실제로 열지 않으므로 빠르고(LED도 안 켜짐), 장치 순서가 OpenCV의 DSHOW 인덱스와 일치한다.
        private void RefreshCameras()
        {
            // 재생·HUD 중엔 상태바를 건드리지 않는다 — [새로고침]이 "카메라 N대"로 덮어써 모드 상태(특히 HUD 의 GDI 대체 안내)가 사라졌다
            bool idle = _mode == SourceMode.None;
            if (idle) Txt_Status.Text = "카메라 검색 중...";

            var names = EnumerateCameraNames();

            _cameraIndices.Clear();
            CameraSelector.Items.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                _cameraIndices.Add(i);                  // DSHOW 인덱스 = 장치 열거 순서
                CameraSelector.Items.Add($"{i}: {names[i]}");
            }

            if (CameraSelector.Items.Count > 0)
                CameraSelector.SelectedIndex = 0;

            Btn_StartCamera.IsEnabled = names.Count > 0;
            if (idle)
                Txt_Status.Text = names.Count > 0
                    ? $"카메라 {names.Count}대 감지됨"
                    : "연결된 카메라 없음";
        }

        private static List<string> EnumerateCameraNames()
        {
            var names = new List<string>();
            try
            {
                var devices = DirectShowLib.DsDevice.GetDevicesOfCat(
                    DirectShowLib.FilterCategory.VideoInputDevice);
                foreach (var d in devices)
                {
                    names.Add(string.IsNullOrWhiteSpace(d.Name) ? "알 수 없는 장치" : d.Name);
                    d.Dispose();
                }
            }
            catch { /* 열거 실패 시 빈 목록 */ }
            return names;
        }

        private async void Btn_StartCamera_Click(object sender, RoutedEventArgs e)
        {
            if (_switchingMode) return;   // 다른 모드 전환이 진행 중 (MainWindow.xaml.cs)
            if (_yolo == null)
            {
                MessageBox.Show("YOLO 모델이 준비되지 않았습니다.");
                return;
            }

            int sel = CameraSelector.SelectedIndex;
            if (sel < 0 || sel >= _cameraIndices.Count)
            {
                MessageBox.Show("사용할 카메라를 선택하세요.");
                return;
            }

            int camIndex = _cameraIndices[sel];

            _switchingMode = true;
            try
            {
                await TearDownSourceAsync();
                OpenCamera(camIndex);
            }
            finally { _switchingMode = false; }
        }

        // 이전 소스는 이미 내렸다. 못 열면 소스 없음 상태로 둔다 (옛 모드가 남지 않게)
        private void OpenCamera(int camIndex)
        {
            try
            {
                var cap = new VideoCapture(camIndex, VideoCaptureAPIs.DSHOW);
                if (!cap.IsOpened())
                {
                    cap.Dispose();
                    ResetToIdle("카메라를 열 수 없습니다");
                    MessageBox.Show("카메라를 열 수 없습니다.");
                    return;
                }

                // 지연(latency) 최소화 — 항상 최신 프레임을 보도록 버퍼 최소화
                try { cap.Set(VideoCaptureProperties.BufferSize, 1); } catch { }

                double fps = cap.Get(VideoCaptureProperties.Fps);
                _fps = (fps > 0 && !double.IsNaN(fps)) ? fps : 30.0;
                _durationMs = 0;
                _vfr = false;
                _seekRefineTargetMs = -1;
                _loadGeneration++;

                _capture = cap;
                _mode = SourceMode.Camera;

                ApplyModeToUi();
                Txt_Status.Text = $"카메라 {camIndex} 실시간";
                StartPlayback();
            }
            catch (Exception ex)
            {
                DisposeCapture();
                ResetToIdle("카메라 시작 실패");
                MessageBox.Show($"카메라 시작 실패:\n{ex.Message}");
            }
        }
    }
}
