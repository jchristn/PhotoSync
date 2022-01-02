using System;
using System.Collections.Generic;
using System.Text;

namespace PhotoSync
{
    public class ExceptionFile
    {
        public string Directory { get; set; } = null;
        public string Filename { get; set; } = null;

        public ExceptionFile()
        {

        }

        public ExceptionFile(string dir, string file)
        {
            Directory = dir;
            Filename = file;
        }
    }
}
