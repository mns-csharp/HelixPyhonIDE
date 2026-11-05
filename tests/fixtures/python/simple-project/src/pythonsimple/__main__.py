import tkinter as tk
from tkinter import ttk


def main() -> None:
    root = tk.Tk()
    root.title("PythonSimple")
    ttk.Label(root, text="Hello, Tkinter!").pack(padx=24, pady=12)
    ttk.Button(root, text="Close", command=root.destroy).pack(padx=24, pady=12)
    root.mainloop()


if __name__ == "__main__":
    main()
