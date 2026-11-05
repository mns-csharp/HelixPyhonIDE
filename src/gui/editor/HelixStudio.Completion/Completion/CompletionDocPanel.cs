using System;
using System.Drawing;
using System.Windows.Forms;

namespace HelixPythonNamespace.Editor.Completion
{
    /// <summary>Non-activating documentation popup shown beside completion lists.</summary>
    public sealed class CompletionDocPanel : IDisposable
    {
        private readonly NoActivateForm _host;
        private readonly Label _title;
        private readonly Label _body;
        private bool _disposed;

        public CompletionDocPanel()
        {
            _host = new NoActivateForm();
            _title = _host.TitleLabel;
            _body = _host.BodyLabel;
        }

        public void Show(
            CompletionItem item, Point screenAnchor, Form owner = null,
            Rectangle avoidBounds = default(Rectangle))
        {
            if (_disposed || item == null) { Hide(); return; }
            if (string.IsNullOrEmpty(item.Detail) && string.IsNullOrEmpty(item.Documentation)) { Hide(); return; }
            _title.Text = item.Label;
            _body.Text = (item.Detail ?? string.Empty) +
                (string.IsNullOrEmpty(item.Documentation) ? string.Empty : "\r\n\r\n" + item.Documentation);
            if (owner != null && _host.Owner != owner)
            {
                try { _host.Owner = owner; } catch { }
            }
            Rectangle area = Screen.FromPoint(screenAnchor).WorkingArea;
            Size desiredSize = CalculateDesiredSize(
                _title.Text, _body.Text, _title.Font, _body.Font, area);
            int width = desiredSize.Width;
            int height = desiredSize.Height;
            _host.ClientSize = new Size(width, height);
            _title.Width = width - 16;
            _host.BodyPanel.Bounds = new Rectangle(8, 28, width - 16, height - 36);
            _body.MaximumSize = new Size(width - 36, 0);
            _host.Location = CalculateLocation(area, new Size(width, height), screenAnchor, avoidBounds);
            if (!_host.Visible) _host.Show();
        }

        public static Size CalculateDesiredSize(
            string title, string body, Font titleFont, Font bodyFont, Rectangle area)
        {
            int maximumWidth = Math.Max(120, Math.Min(600, area.Width - 32));
            int minimumWidth = Math.Min(220, maximumWidth);
            int maximumHeight = Math.Max(72, Math.Min(400, area.Height - 32));

            Size titleSize = TextRenderer.MeasureText(
                title ?? string.Empty, titleFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            Size unwrappedBody = TextRenderer.MeasureText(
                body ?? string.Empty, bodyFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int width = Math.Max(titleSize.Width + 16, unwrappedBody.Width + 36);
            width = Math.Max(minimumWidth, Math.Min(maximumWidth, width));

            int bodyWidth = Math.Max(80, width - 36);
            Size wrappedBody = TextRenderer.MeasureText(
                body ?? string.Empty, bodyFont, new Size(bodyWidth, maximumHeight),
                TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            int height = 44 + Math.Max(bodyFont.Height, wrappedBody.Height);
            height = Math.Max(72, Math.Min(maximumHeight, height));
            return new Size(width, height);
        }

        public static Point CalculateLocation(
            Rectangle area, Size size, Point screenAnchor, Rectangle avoidBounds)
        {
            int clampedX = Math.Max(area.Left, Math.Min(screenAnchor.X, area.Right - size.Width));
            int clampedY = screenAnchor.Y + size.Height <= area.Bottom
                ? screenAnchor.Y
                : Math.Max(area.Top, screenAnchor.Y - size.Height);
            if (avoidBounds.IsEmpty)
                return new Point(clampedX, clampedY);

            const int gap = 8;
            var candidates = new[]
            {
                new Point(avoidBounds.Right + gap, avoidBounds.Top),
                new Point(avoidBounds.Left - gap - size.Width, avoidBounds.Top),
                new Point(clampedX, avoidBounds.Bottom + gap),
                new Point(clampedX, avoidBounds.Top - gap - size.Height)
            };
            foreach (Point candidate in candidates)
            {
                var bounds = new Rectangle(candidate, size);
                if (area.Contains(bounds) && !bounds.IntersectsWith(avoidBounds))
                    return candidate;
            }

            // Extremely small work areas may fit neither popup without overlap. Keep the
            // completion list readable and put documentation in the largest remaining region.
            int below = area.Bottom - avoidBounds.Bottom;
            int above = avoidBounds.Top - area.Top;
            int y = below >= above
                ? Math.Min(area.Bottom - size.Height, avoidBounds.Bottom + gap)
                : Math.Max(area.Top, avoidBounds.Top - gap - size.Height);
            return new Point(clampedX, y);
        }

        public void Hide()
        {
            if (!_disposed && _host.Visible) _host.Hide();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _host.Dispose(); } catch { }
        }
    }
}
