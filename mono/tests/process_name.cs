using System;
using System.Diagnostics;

public class Test: IDisposable {

	public void Dispose() {
	}
	
	
	public static int Main() {
		try {
			var x = Process.GetCurrentProcess().ProcessName;
		}
		catch (Exception e) {
			Console.WriteLine("Exception: " + e);
			return 1;
		}
		return 0;
	}

}

