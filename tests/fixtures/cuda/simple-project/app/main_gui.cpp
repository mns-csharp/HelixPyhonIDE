// Native Win32 translation of __main__.py's tkinter hello-world window
// (a titled window with a label and a Close button).
//
// This file is plain C++ (not .cu), even though the rest of this fixture is
// built as CUDA/.cu translation units: nvcc has no objection to compiling
// Win32 UI code as ordinary host C++ inside a .cu file, but there is zero
// device relevance here (no kernels, no numeric work to accelerate), so it
// is built with the plain CXX compiler instead — the same way any other
// pure-host translation unit is handled in a mixed CXX/CUDA CMake project.

#include <windows.h>

#pragma comment(lib, "user32.lib")

namespace {

constexpr wchar_t kClassName[] = L"PythonSimpleWindow";
constexpr int kCloseButtonId = 1;

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
    switch (msg) {
    case WM_COMMAND:
        if (LOWORD(wParam) == kCloseButtonId) {
            DestroyWindow(hwnd);
        }
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    default:
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}

} // namespace

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR, int nCmdShow) {
    WNDCLASSW wc{};
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = kClassName;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
    RegisterClassW(&wc);

    // root.title("PythonSimple")
    HWND hwnd = CreateWindowExW(
        0, kClassName, L"PythonSimple", WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 320, 160,
        nullptr, nullptr, hInstance, nullptr);
    if (!hwnd) {
        return 0;
    }

    // ttk.Label(root, text="Hello, Tkinter!")
    CreateWindowExW(0, L"STATIC", L"Hello, Tkinter!",
                     WS_CHILD | WS_VISIBLE,
                     24, 12, 260, 24, hwnd, nullptr, hInstance, nullptr);

    // ttk.Button(root, text="Close", command=root.destroy)
    CreateWindowExW(0, L"BUTTON", L"Close",
                     WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                     24, 56, 100, 28, hwnd,
                     reinterpret_cast<HMENU>(static_cast<INT_PTR>(kCloseButtonId)),
                     hInstance, nullptr);

    ShowWindow(hwnd, nCmdShow);
    UpdateWindow(hwnd);

    // root.mainloop()
    MSG msg;
    while (GetMessage(&msg, nullptr, 0, 0)) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }
    return 0;
}
