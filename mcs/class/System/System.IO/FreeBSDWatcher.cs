// 
// System.IO.FreeBSDWatcher.cs: interface with FreeBSD kevent mechanism
//
// Authors:
//	Geoff Norton (gnorton@customerdna.com)
//	Cody Russell (cody@xamarin.com)
//	Alexis Christoforides (lexas@xamarin.com)
//	Ivan Radovanovic (radovanovic@gmail.com)
//
// (c) 2004 Geoff Norton
// Copyright 2014 Xamarin Inc
// Copyright 2016 Ivan Radovanovic
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

// uncomment to debug
//#define DEBUG_FREEBSD_WATCHER
using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Threading;
using System.Text;
using System.Runtime.InteropServices;

namespace System.IO {

	/* 
	 * BASIC IDEA:
	 *  * obtain kqueue descriptor to listen for events
	 *  * collect descriptors of all files we care about in list
	 *  * we do blocking wait until something happens on one of file descriptors
	 *    or on EVFILT_USER descriptor
	 *  * we signal termination using EVFILT_USER mechanism
	 * (in theory quite simple)
	 */
	class FreeBSDMonitor: IDisposable
	{
		const int O_RDONLY = 0;
		const int F_OK = 0;
		const int EINTR = 4;

		const int _StopperEvent = 1;
		// data exclusively used by main thread
		Thread _KeventThread;
		// data used by both main and watcher thread
		AutoResetEvent _StartedRunning = new AutoResetEvent(false);
		FileSystemWatcher _Watcher;
		volatile int _kqueue;
		volatile Exception _ProblemStarting;
		volatile bool _Started;

		public FreeBSDMonitor(FileSystemWatcher fsw) {
			_Started = false;
			_ProblemStarting = null;
			_KeventThread = null;

			_Watcher = fsw;
		}

		public void Start() {
			if (_Started)
				return;

			_kqueue = kqueue();
			if (_kqueue == -1)
				throw new IOException(String.Format("FreeBSDWatcher: kqueue() error at init, error code = '{0}'", Marshal.GetLastWin32Error()));
			
			_KeventThread = new Thread(Run);
			_KeventThread.IsBackground = true;
			_KeventThread.Start();

			_StartedRunning.WaitOne();

			if (_ProblemStarting != null)
				throw _ProblemStarting;
		}

		public void Stop() {
			if (!_Started) {
				close(_kqueue);
				_kqueue = -1;
				return;
			}

			_Started = false;	// to be able to break dir. scanning

			var stopper = new kevent_struct();
			EV_SET(ref stopper, _StopperEvent, EventFilter.User, EventFlags.None, FilterFlags.NoteTrigger, IntPtr.Zero, IntPtr.Zero);
			if (kevent(_kqueue, new kevent_struct[]{stopper}, 1, null, 0, IntPtr.Zero) < 0) {
				// there was error signaling - quite impossible
				throw new IOException(string.Format("FreeBSDWatcher: Error stopping monitor: {0}", Marshal.GetLastWin32Error()));
			}

			_KeventThread.Join();

			close(_kqueue);
			_kqueue = -1;
		}

		public void Dispose() {
			if (_kqueue == -1)
				return;
			else
				Stop();
		}

		void Run() {
			// first set up our stopper event
			var stopper = new kevent_struct();
			EV_SET(ref stopper, _StopperEvent, EventFilter.User, EventFlags.Add, FilterFlags.None, IntPtr.Zero, IntPtr.Zero);
			if (kevent(_kqueue, new kevent_struct[]{stopper}, 1, null, 0, IntPtr.Zero) < 0) {
				_ProblemStarting = new IOException(string.Format("FreeBSDWatcher: Error setting up stop event: {0}", Marshal.GetLastWin32Error()));
				_StartedRunning.Set();
				return;
			}

			// all data we need is allocated on stack here, so we don't worry about sync
			Dictionary<int, PathData> fds = new Dictionary<int, PathData>();
			Dictionary<uint, PathData> inodes = new Dictionary<uint, PathData>();
			bool recursive = _Watcher.IncludeSubdirectories;
			string base_dir = _Watcher.FullPath;
			var events_buf = new kevent_struct[32];
			int received = 0;
			timespec zero_time;
			bool watched_tree_destroyed = false;
			var pattern = _Watcher.Pattern;
			bool need_file_fd = 
				(_Watcher.NotifyFilter & NotifyFilters.LastAccess) != 0 ||
				(_Watcher.NotifyFilter & NotifyFilters.Size) != 0 ||
				(_Watcher.NotifyFilter & NotifyFilters.Security) != 0 ||
				(_Watcher.NotifyFilter & NotifyFilters.Attributes) != 0 ||
				(_Watcher.NotifyFilter & NotifyFilters.LastWrite) != 0;
			bool report_dirname_change = (_Watcher.NotifyFilter & NotifyFilters.DirectoryName) != 0;

			if (_Watcher.FullPath != "/" && _Watcher.FullPath.EndsWith ("/", StringComparison.Ordinal))
				base_dir = _Watcher.FullPath.Substring (0, _Watcher.FullPath.Length - 1);

			zero_time.tv_sec = IntPtr.Zero;
			zero_time.tv_nsec = IntPtr.Zero;

			_Started = true;
			
			_ProblemStarting = CollectAndMonitorDescriptors(base_dir, pattern, need_file_fd, recursive, this, fds, inodes);

			// make sure to tell others we are running
			_StartedRunning.Set();
			
			if (_ProblemStarting != null)
				return;

			/*
			 * System behavior
			 * Action              Directory From   Directory To   File            Notes
			 * -----------------------------------------------------------------------------------
			 * File move           Write            Write          Rename          Can move out of watched space
			 * File delete         Write            N/A            Delete          Delete means unlink is called on given inode, not that 
			 *                                                                     file is really deleted - if more than one file links 
			 *                                                                     to it all will receive delete when one is deleted
			 * File create         N/A              Write          -
			 * File changed        -                -              Write,Extend
			 * Dir move            Write            Write          Rename          Can move out of watched space, no recursive events on 
			 *                                                                     files inside
			 * Dir delete          Write            N/A            Delete          Rec events on files inside
			 * Dir create          N/A              Write          -
			 * File move (outside) N/A              Write          -
			 * Dir move (outside)  N/A              Write          -
			 * 
			 * Obviously all operations but actual change on file are reported on directories
			 * so almost everything can be discovered by scanning dirs.
			 */

			// do actual monitoring
			// we are checking that _Started flag is set as well (in case we have
			// gazillion events pending in queue, stop event could come 
			// unacceptably late)
		watcher_loop:
			while (_Started && (received = kevent(_kqueue, null, 0, events_buf, events_buf.Length, IntPtr.Zero)) > 0) {
				List<kevent_struct> events = new List<kevent_struct>(received);

				do {
					for (int i = 0; i < received; i++) {
						// ASAP check if we were asked to stop 
						if (events_buf[i].filter == EventFilter.User && events_buf[i].ident.ToInt32() == _StopperEvent)
							goto exit_watcher;
						else
							events.Add(events_buf[i]);
					}

					// in case buffer was filled check if there were more events pending - but don't wait
					if (received == events_buf.Length)
						received = kevent(_kqueue, null, 0, events_buf, events_buf.Length, ref zero_time);
					else
						break;
				}
				while (received > 0);

				/*
				 * we keep two sets of FS objects to check
				 *  - directory descriptors (we need to rescan them)
				 *  - inode numbers for changed files
				 */
				Hashtable dir_fds = new Hashtable();
				Hashtable changed_files_inodes = new Hashtable();
				/*
				 * Data to record changes - two sets of inodes and
				 * list of events we detected so far, grouped by inode (in
				 * order to prevent duplicating of events for single file/dir)
				 */
				Hashtable deleted_inodes = new Hashtable();
				Hashtable created_inodes = new Hashtable();
				Dictionary<uint, List<FileSystemEventArgs>> detected_events = new Dictionary<uint, List<FileSystemEventArgs>>();

#if DEBUG_FREEBSD_WATCHER
				Console.WriteLine("Got {0} events", events.Count);
#endif
				for (int i = 0; i < events.Count; i++) {
					var evnt = events[i];
					int fd = evnt.ident.ToInt32();

#if DEBUG_FREEBSD_WATCHER
					Console.WriteLine("Got {0}", evnt.ToString());
#endif
					
					if ((evnt.filter & EventFilter.Vnode) != 0) {
#if DEBUG_FREEBSD_WATCHER						
						Console.WriteLine("Event {0} on {1} ({2})", evnt.fflags, fds[fd].Path, fds[fd].Inode);
#endif
						if (fds[fd].IsDirectory)
							dir_fds.Add(fd, 0);
						else
							if ((evnt.fflags & FilterFlags.VNodeAttrib) != 0 || 
								(evnt.fflags & FilterFlags.VNodeExtend) != 0 ||
								(evnt.fflags & FilterFlags.VNodeWrite) != 0)
								changed_files_inodes.Add(fds[fd].Inode, 0);

						/*
						 * if entire watched tree is destroyed we still want to
						 * report all files deleted (we are not just terminating)
						 */
						if (fds[fd].Inode == 0 && (evnt.fflags & FilterFlags.VNodeDelete) != 0)
							watched_tree_destroyed = true;
					}
				}

				// XXX use ref for all parameters where contents can change, 
				// to make it more obvious when reading code
				foreach (int dir_fd in dir_fds.Keys)
					RescanDir(this, base_dir, pattern, need_file_fd, recursive, dir_fd, ref fds, ref inodes, ref deleted_inodes, ref created_inodes, ref detected_events);

				// add changed files to the event list as well
				foreach (uint file_inode in changed_files_inodes.Keys) {
					var data = inodes[file_inode];

					AddFSEvent(base_dir, data, WatcherChangeTypes.Changed, ref detected_events);
				}

				// order events properly
				List<uint> event_inodes = new List<uint>(detected_events.Keys);
				event_inodes.Sort((a, b) => inodes[a].Path.CompareTo(inodes[b].Path));

				List<int> new_descriptors = new List<int>();
				foreach (uint created_inode in created_inodes.Keys) {
					var data = inodes[created_inode];

					if (!data.IsDirectory && need_file_fd && pattern.IsMatch(data.Filename) && data.Fd < 0) {
						// monitor file contents if name matches pattern
						int fd = open(data.Path, O_RDONLY, 0);
						if (fd < 0)
							continue;
						data.Fd = fd;
						fds.Add(fd, data);
						new_descriptors.Add(fd);
#if DEBUG_FREEBSD_WATCHER
						Console.WriteLine("Now watching {0} at {1}", data.Path, data.Fd);
#endif
					}
				}

				// pass all new descriptors in one bulk
				if (new_descriptors.Count > 0)
					MonitorDescriptors(_kqueue, new_descriptors.ToArray());

				/* 
				 * XXX all moved inodes will be reported either:
				 *  - 0 times - if destination directory got scanned first 
				 *  - 2 times - once in deleted and once in created, if source 
				 *           was scanned first 
				 * therefore we need to distinguish between them and really deleted
				 */
				var really_deleted = ExceptWith<uint>(deleted_inodes, created_inodes);
				List<int> deleted_descriptors = new List<int>();
				foreach (var inode in really_deleted) {
					var data = inodes[inode];
					if (data.Fd >= 0) {
						deleted_descriptors.Add(data.Fd);
						fds.Remove(data.Fd);
						data.Fd = -1;
					}
#if DEBUG_FREEBSD_WATCHER
					Console.WriteLine("Not watching {0} any more", data.Path);
#endif
					inodes.Remove(data.Inode);
				}

				// remove all old descriptors in one bulk
				UnmonitorDescriptors(_kqueue, deleted_descriptors.ToArray());

				// we are sending events from this thread only - in order to prevent
				// user handler from experiencing race conditions
				foreach (var changed_inode in event_inodes)
					foreach (var evnt in detected_events[changed_inode]) {

						if (evnt is RenamedEventArgs) {
							// for renamed event we need to check if we should either
							//  - stop monitoring file (if new name doesn't match)
							//  - start monitoring file (if new name matches)
							var data = inodes[changed_inode];
							if (!data.IsDirectory)
								if (data.Fd != -1 && !pattern.IsMatch(data.Filename)) {
									UnmonitorDescriptors(_kqueue, data.Fd);
									fds.Remove(data.Fd);
									data.Fd = -1;
								}
								else if (need_file_fd && pattern.IsMatch(data.Filename) && data.Fd < 0) {
									int fd = open(data.Path, O_RDONLY, 0);
									if (fd < 0)
										continue;
									data.Fd = fd;
									fds.Add(fd, data);
									// we hope this is not very frequent operation
									// so we don't create bulk
									MonitorDescriptors(_kqueue, fd);
#if DEBUG_FREEBSD_WATCHER
									Console.WriteLine("Now watching {0} at {1}", data.Path, data.Fd);
#endif
								}
						}

						// check if user asked to be notified about this one
						if (pattern.IsMatch(evnt.Name))
							_Watcher.DispatchEvent(evnt);
						else if (evnt is RenamedEventArgs) {
							var data = inodes[changed_inode];
							if (report_dirname_change && data.IsDirectory)
								_Watcher.DispatchEvent(evnt);
							else {
								var revnt = (RenamedEventArgs)evnt;
								if (pattern.IsMatch(revnt.OldName))
									_Watcher.DispatchEvent(evnt);
							}
						}
					}
#if DEBUG_FREEBSD_WATCHER
				GetFds(fds);
#endif
				if (watched_tree_destroyed)
					goto exit_watcher;
			}

			// in case we were interrupted in our call we want to try again
			int errno = Marshal.GetLastWin32Error();
			if (_Started && received == -1 && errno == EINTR) 
				goto watcher_loop;

		exit_watcher:
			CloseDescriptors(fds.Keys);

			// mark ourselves as stopped
			_Started = false;
		}

		static T[] ExceptWith<T>(Hashtable a, Hashtable b) {
			List<T> retval = new List<T>();
			foreach (T x in a.Keys)
				if (!b.ContainsKey(x))
					retval.Add(x);

			return retval.ToArray();
		}

		static Hashtable HashtableFromCollection(ICollection col) {
			Hashtable retval = new Hashtable();
			foreach (var x in col)
				retval.Add(x, 0);

			return retval;
		}

		/// <summary>
		/// Rescans directory without recursing.
		/// </summary>
		/// <param name="dir_fd">Dir fd.</param>
		/// <param name="fds">Fds.</param>
		static void RescanDir(FreeBSDMonitor monitor, string base_dir, SearchPattern2 pattern, bool need_file_fd, bool recursive_watcher, int dir_fd, ref Dictionary<int, PathData> fds, ref Dictionary<uint, PathData> inodes,
			ref Hashtable deleted_inodes, ref Hashtable created_inodes, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			var current_dir = fds[dir_fd];
			var dir_handle = fdopendir(dup(dir_fd));
			bool dir_changed = false;

			// XXX we have to rewind dir since we had its descriptor duplicated
			rewinddir(dir_handle);

			IntPtr file_handle;
			var old_contents = HashtableFromCollection(current_dir.Files.Keys);
			var new_contents = new Hashtable();
			while (monitor._Started && (file_handle = readdir(dir_handle)) != IntPtr.Zero) {
				var file_info = dirent.FromPtr(file_handle);
				var fname = file_info.Name;

				if (fname == "." || fname == "..")
					continue;

				new_contents.Add(file_info.d_fileno, 0);

				if (!old_contents.Contains(file_info.d_fileno)) {
					// created new file or dir
					dir_changed = true;

#if DEBUG_FREEBSD_WATCHER
					Console.WriteLine("Created: " + fname);
#endif
					if (inodes.ContainsKey(file_info.d_fileno)) {
						// it is actually just moved from somewhere within watched tree
						var data = inodes[file_info.d_fileno];

						if (data.IsDirectory) {
							if (data.ParentDir != null) {
								// destination dir first got chance to handle event
								ReportSubtreeMoved(base_dir, data, current_dir, ref events);
							}
							else {
								// already disconnected - just attach it to current...
								current_dir.Files.Add(data.Inode, data);
								data.ParentDir = current_dir;

								// and pretend it was just created (old parent already reported it deleted)
								AddFSEvent(base_dir, data, WatcherChangeTypes.Created, ref events);
								created_inodes.Add(file_info.d_fileno, 0);

								ReportSubtreeCreated(base_dir, data, ref created_inodes, ref events);
							}
						}
						else {
							if (data.ParentDir != null) {
								// if it wasn't disconnected from old parent we do that now,
								// old parent didn't have chance to report it as deleted
								AddFSEvent(base_dir, data, WatcherChangeTypes.Deleted, ref events);
								data.ParentDir.Files.Remove(data.Inode);
							}
							else {
								// old parent already reported it deleted, so we have to undo that
								created_inodes.Add(file_info.d_fileno, 0);
							}

							current_dir.Files.Add(data.Inode, data);
							data.ParentDir = current_dir;

							AddFSEvent(base_dir, data, WatcherChangeTypes.Created, ref events);
						}
					}
					else {
						// really new (at least for us watching this tree)
						if (recursive_watcher && file_info.IsDirectory) {
							Dictionary<uint, PathData> new_inodes = new Dictionary<uint, PathData>();

							CollectAndMonitorDescriptors(System.IO.Path.Combine(current_dir.Path, fname), pattern, need_file_fd, true, monitor, fds, new_inodes);

							// root watched dir gets inode 0
							var data = new_inodes[0];

							// we fix that now
							data.Inode = file_info.d_fileno;
							data.Filename = fname;

							data.ParentDir = current_dir;
							current_dir.Files.Add(data.Inode, data);

							foreach (var new_node in new_inodes) {
								AddFSEvent(base_dir, new_node.Value, WatcherChangeTypes.Created, ref events);

								inodes.Add(new_node.Value.Inode, new_node.Value);
							}
						}
						else {
							var data = new PathData(fname, file_info.IsDirectory, -1, file_info.d_fileno, current_dir);

							AddFSEvent(base_dir, data, WatcherChangeTypes.Created, ref events);

							current_dir.Files.Add(data.Inode, data);
							inodes.Add(data.Inode, data);
							created_inodes.Add(data.Inode, 0);
						}
					}
				}
				else {
					var file = current_dir.Files[file_info.d_fileno];

					// existing file - check its name
					if (file.Filename != fname) {
						dir_changed = true;

						var old_relpath = file.RelPath;
						file.Filename = fname;

						AddRenamedEvent(file, current_dir.Path, file.RelPath, old_relpath, ref events);
					}
					// nothing changed
				}
			}

#if DEBUG_FREEBSD_WATCHER
			Console.WriteLine("**** Rescaned {0}, old_count={1}, new_count={2}", current_dir.Filename, old_contents.Count, new_contents.Count);
#endif
			// lets mark everything we didn't find as deleted
			var locally_deleted_inodes = ExceptWith<uint>(old_contents, new_contents);
			foreach (var inode in locally_deleted_inodes) {
				dir_changed = true;
				var deleted = current_dir.Files[inode];

				AddFSEvent(base_dir, deleted, WatcherChangeTypes.Deleted, ref events);

				// disconnect
				current_dir.Files.Remove(inode);

#if DEBUG_FREEBSD_WATCHER
				Console.WriteLine("Deleted: {0}", deleted.Path);
#endif
				if (deleted.IsDirectory)
					ReportSubtreeDeleted(base_dir, deleted, ref deleted_inodes, ref events);	// recurse
				else
					deleted_inodes.Add(deleted.Inode, 0);
				
				deleted.ParentDir = null;
			}

			if (dir_changed)
				// XXX for some reason MS doesn't report changes in root of watched tree
				// so we have to mimic that behavior 
				if (current_dir.ParentDir != null)
					AddFSEvent(base_dir, current_dir, WatcherChangeTypes.Changed, ref events);

			closedir(dir_handle);
		}

		static void AddFSEvent(string base_dir, PathData file, WatcherChangeTypes change, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			List<FileSystemEventArgs> changes;

			if (!events.TryGetValue(file.Inode, out changes)) {
				changes = new List<FileSystemEventArgs>();
				events.Add(file.Inode, changes);
			}

			foreach (var evt in changes)
				if (evt.ChangeType == change)
					return;

			changes.Add(new FileSystemEventArgs(change, base_dir, file.RelPath));
		}

		static void AddRenamedEvent(PathData file, string directory, string rel_path, string old_relpath, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			List<FileSystemEventArgs> changes;

			if (!events.TryGetValue(file.Inode, out changes)) {
				changes = new List<FileSystemEventArgs>();
				events.Add(file.Inode, changes);
			}

			changes.Add(new RenamedEventArgs(WatcherChangeTypes.Renamed, directory, rel_path, old_relpath));
		}

		static void ReportSubtreeDeleted(string base_dir, PathData dir, ref Hashtable deleted_inodes, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			foreach (var file in dir.Files.Values) {
				AddFSEvent(base_dir, file, WatcherChangeTypes.Deleted, ref events);

				if (deleted_inodes != null)
					deleted_inodes.Add(file.Inode, 0);

				if (file.IsDirectory)
					ReportSubtreeDeleted(base_dir, file, ref deleted_inodes, ref events);
			}

			if (deleted_inodes != null)
				deleted_inodes.Add(dir.Inode, 0);
		}

		static void ReportSubtreeCreated(string base_dir, PathData dir, ref Hashtable created_inodes, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			foreach (var file in dir.Files.Values) {
				AddFSEvent(base_dir, file, WatcherChangeTypes.Created, ref events);

				if (created_inodes != null)
					created_inodes.Add(file.Inode, 0);

				if (file.IsDirectory)
					ReportSubtreeCreated(base_dir, file, ref created_inodes, ref events);
			}
		}

		static void ReportSubtreeMoved(string base_dir, PathData dir, PathData new_parent, ref Dictionary<uint, List<FileSystemEventArgs>> events) {
			Hashtable dummy = null;

			ReportSubtreeDeleted(base_dir, dir, ref dummy, ref events);

			AddFSEvent(base_dir, dir, WatcherChangeTypes.Deleted, ref events);

			dir.ParentDir.Files.Remove(dir.Inode);

			dir.ParentDir = new_parent;

			new_parent.Files.Add(dir.Inode, dir);

			AddFSEvent(base_dir, dir, WatcherChangeTypes.Created, ref events);

			ReportSubtreeCreated(base_dir, dir, ref dummy, ref events);
		}

		static Exception CollectAndMonitorDescriptors(string directory, SearchPattern2 pattern, bool need_file_fd, bool recurse, FreeBSDMonitor monitor, Dictionary<int, PathData> fds, 
			Dictionary<uint, PathData> inodes) {
			List<int> descriptors = new List<int>();
			List<int> subdirs = new List<int>();

			int fd = open(directory, O_RDONLY, 0);
			if (fd < 0)
				return new IOException(string.Format("FreeBSDWatcher: Error collecting descriptors: {0}", Marshal.GetLastWin32Error()));

			var path = new PathData(directory, true, fd, 0, null);	// for root dir we don't care about inode number

			fds.Add(fd, path);
			inodes.Add(path.Inode, path);
			descriptors.Add(fd);

			subdirs.Add(fd);

			// doing BFS to collect files
			int current_subdir = 0;
			while (current_subdir < subdirs.Count) {
				fd = subdirs[current_subdir];
				current_subdir++;

				// descriptor is not leaking because closedir is closing it
				var dir_handle = fdopendir(dup(fd));
				var dir_path = fds[fd];

				IntPtr fptr;
				while (monitor._Started && (fptr = readdir(dir_handle)) != IntPtr.Zero) {
					var file = dirent.FromPtr(fptr);
					var fname = file.Name;

					if (fname == "." || fname == "..")
						continue;

					string file_path = Path.Combine(dir_path.Path, fname);

					path = new PathData(fname, file.IsDirectory, -1, file.d_fileno, dir_path);

					dir_path.Files.Add(path.Inode, path);
					inodes.Add(path.Inode, path);

					if (!recurse && file.IsDirectory)
						continue;

					if ((recurse && file.IsDirectory) || (need_file_fd && pattern.IsMatch(fname))) {
						fd = open(file_path, O_RDONLY, 0);
						if (fd < 0)
							continue;
						path.Fd = fd;
						fds.Add(fd, path);
						descriptors.Add(fd);
						if (file.IsDirectory)
							subdirs.Add(fd);
					}
				}
				closedir(dir_handle);
			}

			if (monitor._Started) {
				var prob = MonitorDescriptors(monitor._kqueue, descriptors.ToArray());
				if (prob != null)
					return prob;
			}

			return null;
		}

		static Exception MonitorDescriptors(int kqueue_fd, params int[] fds) {
			List<kevent_struct> kevents = new List<kevent_struct>(fds.Length);
			var monitor = new kevent_struct();

#if DEBUG_FREEBSD_WATCHER
			Console.Write("Monitoring: ");
			for (int i = 0; i < fds.Length; i++)
				Console.Write("{0}, ", fds[i]);
			Console.WriteLine();
#endif
			foreach (var fd in fds) {			
				EV_SET(ref monitor, fd, EventFilter.Vnode, EventFlags.Add | EventFlags.Clear | EventFlags.Enable, 
					FilterFlags.VNodeDelete | FilterFlags.VNodeWrite | FilterFlags.VNodeExtend | FilterFlags.VNodeAttrib | 
					FilterFlags.VNodeLink | FilterFlags.VNodeRename | FilterFlags.VNodeRevoke, 
					IntPtr.Zero, IntPtr.Zero);
				kevents.Add(monitor);
			}

			if (kevent(kqueue_fd, kevents.ToArray(), kevents.Count, null, 0, IntPtr.Zero) < 0) 
				return new IOException(string.Format("FreeBSDWatcher: Error monitoring descriptor: {0}", Marshal.GetLastWin32Error()));

			return null;
		}

		static Exception UnmonitorDescriptors(int kqueue_fd, params int[] fds) {
			List<kevent_struct> kevents = new List<kevent_struct>(fds.Length);
			var monitor = new kevent_struct();

			foreach (var fd in fds) {			
				EV_SET(ref monitor, fd, EventFilter.Vnode, EventFlags.Delete, 
					FilterFlags.VNodeDelete | FilterFlags.VNodeWrite | FilterFlags.VNodeExtend | FilterFlags.VNodeAttrib | 
					FilterFlags.VNodeLink | FilterFlags.VNodeRename | FilterFlags.VNodeRevoke, 
					IntPtr.Zero, IntPtr.Zero);
				kevents.Add(monitor);
			}

			if (kevent(kqueue_fd, kevents.ToArray(), kevents.Count, null, 0, IntPtr.Zero) < 0) 
				return new IOException(string.Format("FreeBSDWatcher: Error monitoring descriptor: {0}", Marshal.GetLastWin32Error()));

			for (int i = 0; i < fds.Length; i++)
				close(fds[i]);

			return null;
		}

		static void CloseDescriptors(IEnumerable<int> fds) {
			foreach (var fd in fds)
				close(fd);
		}

		static void EV_SET(ref kevent_struct kev, int ident, EventFilter filter, EventFlags flags, FilterFlags filter_flags, IntPtr data, IntPtr udata) {
			kev.ident = new IntPtr(ident);
			kev.filter = filter;
			kev.flags = flags;
			kev.fflags = filter_flags;
			kev.data = data;
			kev.udata = udata;
		}

#if DEBUG_FREEBSD_WATCHER
		[StructLayout(LayoutKind.Sequential)]
		struct kinfo_file {
			int             kf_structsize;          /* Variable size of record. */
			int             kf_type;                /* Descriptor type. */
			int             kf_fd;                  /* Array index. */
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 356)]
			byte[]          dummy;
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)]
			byte[]          kf_path;                /* Path to file, if any. */

			public int Fd {
				get {
					return kf_fd;
				}
			}
			public string Name {
				get {
					return System.Text.ASCIIEncoding.ASCII.GetString(kf_path).TrimEnd('\0');
				}
			}

		};

		static void GetFds(Dictionary<int, PathData> fds) {
			Console.WriteLine("Open descriptors:\n----------------------------------------------");
			int count = 0;
			var ret = kinfo_getfile(System.Diagnostics.Process.GetCurrentProcess().Id, out count);
			HashSet<int> fd_hash = new HashSet<int>();
			if (ret != IntPtr.Zero) {
				for (int i = 0; i < count; i++) {
					long ptr = ret.ToInt64() + i * 1392;
					var kif = (kinfo_file)Marshal.PtrToStructure(new IntPtr(ptr), typeof(kinfo_file));
					if (!fds.ContainsKey(kif.Fd))
						Console.Write("NOT FOUND IN HASH ----> ");
					else
						Console.Write("{0} --> ", fds[kif.Fd].Path);
					Console.WriteLine("{0}: {1}", kif.Fd, kif.Name);
					fd_hash.Add(kif.Fd);
				}
				free(ret);
			}

			foreach (var fd in fds.Keys)
				if (!fd_hash.Contains(fd))
					Console.WriteLine("Hanged descriptor in hash: " + fd);
		}
#endif

		#region Constants and structures copied from system include files
		[Flags]
		enum EventFlags : ushort {
			None        = 0,		// XXX
			Add         = 0x0001,
			Delete      = 0x0002,
			Enable      = 0x0004,
			Disable     = 0x0008,
			OneShot     = 0x0010,
			Clear       = 0x0020,
			Receipt     = 0x0040,
			Dispatch    = 0x0080,

			Drop        = 0x1000,
			Flag1       = 0x2000,
			SystemFlags = 0xf000,

			// Return values.
			EOF         = 0x8000,
			Error       = 0x4000,
		}

		enum EventFilter : short {
			Read     = -1,
			Write    = -2,
			Aio      = -3,
			Vnode    = -4,
			Proc     = -5,
			Signal   = -6,
			Timer    = -7,
			NetDev   = -8,
			FS       = -9,
			Lio      = -10,
			User     = -11
		}

		[Flags]
		enum FilterFlags : uint {
			None              = 0,

			VNodeDelete       = 0x00000001,
			VNodeWrite        = 0x00000002,
			VNodeExtend       = 0x00000004,
			VNodeAttrib       = 0x00000008,
			VNodeLink         = 0x00000010,
			VNodeRename       = 0x00000020,
			VNodeRevoke       = 0x00000040,

			NoteTrigger       = 0x01000000, 
			NoteFFAnd         = 0x40000000,
			NoteFFOr          = 0x80000000,
			NoteFFCopy        = 0xc0000000,
			NoteFFCtrlMask    = 0xc0000000,
			NoteFFlagsMask    = 0x00ffffff,
		}

		[StructLayout(LayoutKind.Sequential)]
		struct kevent_struct {
			public IntPtr ident;
			public EventFilter filter;
			public EventFlags flags;
			public FilterFlags fflags;
			public IntPtr data;
			public IntPtr udata;

			public override string ToString() {
				return string.Format("[ ident = {0}, filter = {1} ({6}), flags = {2} ({7:x8}), fflags = {3} ({8:x8}), data = {4}, udata = {5} ]",
					ident, filter, flags, fflags, data, udata, (short)filter, (int)flags, (uint)fflags);
			}
		}

		[StructLayout(LayoutKind.Sequential)]
		struct timespec {
			public IntPtr tv_sec;
			public IntPtr tv_nsec;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct dirent {
			const int DT_DIR = 4;
			public uint d_fileno;       /* file number of entry */
			ushort d_reclen;            /* length of this record */
			byte d_type;                /* file type, see below */
			byte d_namlen;              /* length of string in d_name */
			[MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
			byte[] d_name;              /* name must be no longer than 256 */

			public string Name {
				get {
					return System.Text.ASCIIEncoding.ASCII.GetString(d_name);
				}
			}

			static public dirent FromPtr(IntPtr ptr) {
				var retval = (dirent)Marshal.PtrToStructure(ptr, typeof(dirent));
				var name_bytes = retval.d_name;
				retval.d_name = new byte[retval.d_namlen];
				Array.Copy(name_bytes, retval.d_name, retval.d_namlen);
				return retval;
			}

			public bool IsDirectory {
				get {
					return (d_type & DT_DIR) != 0;
				}
			}
		};

		class PathData {
			// top directory will have full path here instead of just filename!
			public string Filename;
			public bool IsDirectory;
			public int Fd;
			public uint Inode;
			public Dictionary<uint, PathData> Files;
			public PathData ParentDir;

			public string Path {
				get {
					if (ParentDir != null)
						return System.IO.Path.Combine(ParentDir.Path, Filename);
					else
						return Filename;
				}
			}

			public string RelPath {
				get {
					if (ParentDir == null)
						return ".";
					else if (ParentDir.ParentDir == null)
						return Filename;
					else
						return System.IO.Path.Combine(ParentDir.RelPath, Filename);
				}
			}

			public PathData(string filename, bool is_directory, int fd, uint inode, PathData parent_dir) {
				Filename = filename;
				IsDirectory = is_directory;
				Fd = fd;
				Inode = inode;
				ParentDir = parent_dir;
				if (IsDirectory)
					Files = new Dictionary<uint, PathData>();
			}
		}
		#endregion
		
		[DllImport("libc")]
		extern static int open(string path, int flags, int mode_t);

		[DllImport("libc")]
		extern static int close(int fd);

		[DllImport("libc")]
		extern static int kqueue();

		[DllImport("libc", SetLastError = true)]
		extern static int kevent(int kq, [In] kevent_struct[] ev, int nchanges, [Out] kevent_struct[] evtlist, int nevents, IntPtr ptr);

		[DllImport ("libc", SetLastError = true)]
		extern static int kevent (int kq, [In] kevent_struct[] ev, int nchanges, [Out] kevent_struct[] evtlist, int nevents, [In] ref timespec time);

		[DllImport("libc")]
		extern static int dup(int fd);

		[DllImport("libc")]
		extern static IntPtr fdopendir(int fd);

		[DllImport("libc")]
		extern static int closedir(IntPtr dir);

		[DllImport("libc")]
		extern static IntPtr readdir(IntPtr dir);

		[DllImport("libc")]
		extern static int rewinddir(IntPtr dir);

		[DllImport("libc")]
		extern static void free(IntPtr m);

		[DllImport("libutil")]
		extern static IntPtr kinfo_getfile(int pid, out int count);
	}

	class FreeBSDWatcher : IFileWatcher
	{
		static bool failed;
		static FreeBSDWatcher instance;
		static Hashtable watches;  // <FileSystemWatcher, FreeBSDMonitor>

		private FreeBSDWatcher ()
		{
		}

		// Locked by caller
		public static bool GetInstance (out IFileWatcher watcher)
		{
			if (failed == true) {
				watcher = null;
				return false;
			}

			if (instance != null) {
				watcher = instance;
				return true;
			}

			watches = Hashtable.Synchronized (new Hashtable ());
			var conn = kqueue();
			if (conn == -1) {
				failed = true;
				watcher = null;
				return false;
			}
			close (conn);

			instance = new FreeBSDWatcher ();
			watcher = instance;
			return true;
		}

		public void StartDispatching (FileSystemWatcher fsw)
		{
			FreeBSDMonitor monitor;

			if (watches.ContainsKey (fsw)) {
				monitor = (FreeBSDMonitor)watches [fsw];
			} else {
				monitor = new FreeBSDMonitor (fsw);
				watches.Add (fsw, monitor);
			}

			monitor.Start ();
		}

		public void StopDispatching (FileSystemWatcher fsw)
		{
			FreeBSDMonitor monitor = (FreeBSDMonitor)watches [fsw];
			if (monitor == null)
				return;

			monitor.Stop ();
		}

		[DllImport ("libc")]
		extern static int close (int fd);

		[DllImport ("libc")]
		extern static int kqueue ();
	}

}

