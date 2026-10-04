using System;

namespace SourceGit.Models
{
    public class Notification
    {
        public static event Action<Notification> Raised;

        public string Group { get; set; }
        public string Message { get; set; }
        public bool IsError { get; set; }

        // Returns the created notification so callers can dismiss it later (see Launcher).
        public static Notification Send(string group, string message, bool isError = false)
        {
            var notification = new Notification
            {
                Group = group,
                Message = message,
                IsError = isError
            };
            Raised?.Invoke(notification);
            return notification;
        }
    }
}
