// A WPF test project does not get System.IO in its implicit usings, unlike a
// plain net9.0 one, and every test file that touches a temp file needs it.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
