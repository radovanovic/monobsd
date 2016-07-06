using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

public class Test: IDisposable {

	ManualResetEvent _Sync = new ManualResetEvent(false);
	int _TimeoutMsec = 1000;
	List<FileSystemEventArgs> _EventsReceived = new List<FileSystemEventArgs>();
	string _Path;
	
	public Test() {
		_Path = Path.GetRandomFileName();
		CreateDirectory(_Path);
		Directory.SetCurrentDirectory(_Path);
	}
	
	public void Dispose() {
		Directory.SetCurrentDirectory("..");
		DeleteDirectory(_Path);
	}

	public int FileCreation() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Created += OnWatcherEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateFile(fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteFile(fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 1;
			}
			else {
				DeleteFile(fname);
				if (WatcherChangeTypes.Created != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 2;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 3;
				}
				if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 4;
				}
				
				return 0;
			}
		}
	}

	public int FileDeletion() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Deleted += OnWatcherEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateFile(fname);
			Thread.Sleep(500);
			DeleteFile(fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 5;
			}
			else {
				if (WatcherChangeTypes.Deleted != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 6;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 7;
				}
				if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 8;
				}
				
				return 0;
			}
		}
	}

	public int FileModification() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Changed += OnWatcherEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateFile(fname);
			Thread.Sleep(1000);
			ModifyFile(fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteFile(fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 9;
			}
			else {
				DeleteFile(fname);
				if (WatcherChangeTypes.Changed != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 10;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 11;
				}
				if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 12;
				}
				
				return 0;
			}
		}
	}

	public int FileRenaming() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Renamed += OnRenamedEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();
			string new_fname = Path.GetRandomFileName();

			CreateFile(fname);
			Thread.Sleep(500);
			RenameFile(fname, new_fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteFile(new_fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 13;
			}
			else {
				DeleteFile(new_fname);
				if (WatcherChangeTypes.Renamed != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 14;
				}
				var re = (RenamedEventArgs)_EventsReceived[0];
				if (fname != re.OldName) {
					Console.WriteLine("Invalid old file name received - expected '{0}', but got '{1}'", fname, re.OldName);
					return 15;
				}
				if (new_fname != re.Name) {
					Console.WriteLine("Invalid new file name received - expected '{0}', but got '{1}'", new_fname, re.Name);
					return 16;
				}
				
				return 0;
			}
		}
	}

	public int DirCreation() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Created += OnWatcherEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateDirectory(fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteDirectory(fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 17;
			}
			else {
				DeleteDirectory(fname);
				if (WatcherChangeTypes.Created != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 18;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 19;
				}
				if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 20;
				}
				
				return 0;
			}
		}
	}

	public int DirDeletion() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Deleted += OnWatcherEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateDirectory(fname);
			Thread.Sleep(500);
			DeleteDirectory(fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 21;
			}
			else {
				if (WatcherChangeTypes.Deleted != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 22;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 23;
				}
				if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 24;
				}
				
				return 0;
			}
		}
	}

	public int DirModification() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Changed += OnWatcherEvent;

			fsw.IncludeSubdirectories = true;
			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();

			CreateDirectory(fname);
			Thread.Sleep(1000);
			CreateFile(fname + Path.DirectorySeparatorChar + "tfile");

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteFile(fname + Path.DirectorySeparatorChar + "tfile");
				DeleteDirectory(fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 25;
			}
			else {
				DeleteFile(fname + Path.DirectorySeparatorChar + "tfile");
				DeleteDirectory(fname);
				if (WatcherChangeTypes.Changed != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 26;
				}
				if (fname != _EventsReceived[0].Name) {
					Console.WriteLine("Invalid file name received - expected '{0}', but got '{1}'", fname, _EventsReceived[0].Name);
					return 27;
				}
				/*if (1 != _EventsReceived.Count) {
					Console.WriteLine("More than one event received");
					return 28;
				}*/
				
				return 0;
			}
		}
	}

	public int DirRenaming() {
		_Sync.Reset();
		_EventsReceived.Clear();

		using (var fsw = new FileSystemWatcher()) {

			fsw.NotifyFilter = NotifyFilters.Attributes | NotifyFilters.CreationTime |
				NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastAccess |
				NotifyFilters.LastWrite | NotifyFilters.Security | NotifyFilters.Size;
			fsw.Filter = "*";
			fsw.Path = ".";

			fsw.Renamed += OnRenamedEvent;

			fsw.EnableRaisingEvents = true;

			string fname = Path.GetRandomFileName();
			string new_fname = Path.GetRandomFileName();

			CreateDirectory(fname);
			Thread.Sleep(500);
			RenameDirectory(fname, new_fname);

			if (!_Sync.WaitOne(_TimeoutMsec)) {
				DeleteDirectory(new_fname);
				Console.WriteLine("No event was received in {0}ms. You might want to increase Timeout if you believe code is correct.", _TimeoutMsec);
				return 29;
			}
			else {
				DeleteDirectory(new_fname);
				if (WatcherChangeTypes.Renamed != _EventsReceived[0].ChangeType) {
					Console.WriteLine("Wrong type of event received: {0}", _EventsReceived[0].ChangeType);
					return 30;
				}
				var re = (RenamedEventArgs)_EventsReceived[0];
				if (fname != re.OldName) {
					Console.WriteLine("Invalid old file name received - expected '{0}', but got '{1}'", fname, re.OldName);
					return 31;
				}
				if (new_fname != re.Name) {
					Console.WriteLine("Invalid new file name received - expected '{0}', but got '{1}'", new_fname, re.Name);
					return 32;
				}
				
				return 0;
			}
		}
	}

	void OnRenamedEvent(object sender, RenamedEventArgs e)	{
		_EventsReceived.Add(e);
		_Sync.Set();
	}

	void OnWatcherEvent(object sender, FileSystemEventArgs e) {
		_EventsReceived.Add(e);
		_Sync.Set();
	}

	void CreateFile(string name) {
		File.Create(name).Dispose();
	}

	void DeleteFile(string name) {
		File.Delete(name);
	}

	void ModifyFile(string name) {
		File.AppendAllText(name, "more text");
	}

	void RenameFile(string name, string new_name) {
		File.Move(name, new_name);
	}

	void CreateDirectory(string name) {
		Directory.CreateDirectory(name);
	}

	void DeleteDirectory(string name) {
		Directory.Delete(name);
	}

	void RenameDirectory(string name, string new_name) {
		Directory.Move(name, new_name);
	}
	
	public static int Main () {
		int result;
		using (var test = new Test()) {
		
			result = test.FileCreation();		
			if (result != 0)
				return result;

			result = test.FileDeletion();
			if (result != 0)
				return result;
			
			result = test.FileModification();
			if (result != 0)
				return result;

			result = test.FileRenaming();
			if (result != 0)
				return result;

			result = test.DirCreation();
			if (result != 0)
				return result;
			
			result = test.DirDeletion();
			if (result != 0)
				return result;
			
			result = test.DirModification();
			if (result != 0)
				return result;
			
			result = test.DirRenaming();
			if (result != 0)
				return result;
		}
		
		return 0;
	}
	
}

