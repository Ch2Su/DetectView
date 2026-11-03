/// <summary>
/// 저장소 루트와 테스트 영상 폴더 — 실행 파일 위치에서 위로 올라가며 DetectView.csproj 가 있는 폴더를 찾는다.
/// 예전엔 영상 폴더가 절대 경로(E:\VS_E\YOLO11_Test\publish_gpu_ver)로 박혀 있어 폴더를 옮기면 깨졌다 (2026-11-03 E:\DetectView 로 옮기며 상대로 바꿈)
/// </summary>
internal static class RepoPaths
{
    public static readonly string Root = FindRoot();

    /// <summary>배포 폴더 — test1~5.mp4 가 같이 있다 (영상은 저장소에 없다)</summary>
    public static readonly string VideoDir = Path.Combine(Root, "publish");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DetectView.csproj"))) return dir.FullName;
        throw new DirectoryNotFoundException("저장소 루트(DetectView.csproj)를 찾지 못했습니다.");
    }
}
