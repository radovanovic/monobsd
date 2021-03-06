//
// System.Security.SecurityState
//
// Author:
//	Sebastien Pouliot  <sebastien@ximian.com>
//
// Copyright (C) 2008-2009 Novell, Inc (http://www.novell.com)
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

namespace System.Security {

	// available in FX2.0 with service pack 1, including the 2.0 shipped as part of FX3.5
	public abstract class SecurityState {

		protected SecurityState ()
		{
		}

		public abstract void EnsureState ();

#if MONO_FEATURE_MULTIPLE_APPDOMAINS
		public bool IsStateAvailable ()
		{
			AppDomainManager adm = AppDomain.CurrentDomain.DomainManager;
			if (adm == null)
				return false;
			return adm.CheckSecuritySettings (this);
		}
#else
		[Obsolete ("SecurityState.IsStateAvailable is not supported on this platform.", true)]
		public bool IsStateAvailable ()
		{
			throw new PlatformNotSupportedException ("SecurityState.IsStateAvailable is not supported on this platform.");
		}
#endif // MONO_FEATURE_MULTIPLE_APPDOMAINS
	}
}


