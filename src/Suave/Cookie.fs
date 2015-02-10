﻿namespace Suave

module Cookie =

  open System
  open System.Text
  open System.Globalization

  open Suave
  open Suave.Types
  open Suave.Http
  open Suave.Logging
  open Suave.Utils

  type CookieLife =
    | Session
    | MaxAge of TimeSpan

  type CookieError =
    | NoCookieFound of string
    | DecryptionError of Crypto.SecretboxDecryptionError

  let parseCookies (s : string) : HttpCookie list =
    s.Split(';')
    |> Array.toList
    |> List.map (fun (cookie : string) ->
        let parts = cookie.Split('=')
        HttpCookie.mkSimple (String.trim parts.[0]) (String.trim parts.[1]))

  let parseResultCookie (s : string) : HttpCookie =
    let parse_expires (str : string) =
      DateTimeOffset.ParseExact(str, "R", CultureInfo.InvariantCulture)
    s.Split(';')
    |> Array.map (fun (x : string) ->
        let parts = x.Split('=')
        if parts.Length > 1 then
          parts.[0].Trim(), parts.[1].Trim()
        else
          parts.[0].Trim(), "")
    |> Array.fold (fun (iter, (cookie : HttpCookie)) -> function
        | name, value when iter = 0 -> iter + 1, { cookie with name = name
                                                               value = value }
        | "Domain", domain          -> iter + 1, { cookie with domain = Some domain }
        | "Path", path              -> iter + 1, { cookie with path = Some path }
        | "Expires", expires        -> iter + 1, { cookie with expires = Some (parse_expires expires) }
        | "HttpOnly", _             -> iter + 1, { cookie with httpOnly = true }
        | "Secure", _               -> iter + 1, { cookie with secure = true }
        | _                         -> iter + 1, cookie)
        (0, { HttpCookie.empty with httpOnly = false }) // default when parsing
    |> snd

  type HttpRequest with

    member x.cookies =
      x.headers
      |> List.filter (fun (name, _) -> name.Equals "cookie")
      |> List.flat_map (snd >> parseCookies)
      |> List.fold (fun cookies cookie ->
          cookies |> Map.add cookie.name cookie)
          Map.empty

  type HttpResult with

    member x.cookies =
      x.headers
      |> List.filter (fst >> (String.eq_ord_ci "Set-Cookie"))
      /// duplicate headers are comma separated
      |> List.flat_map (snd >> String.split ',' >> List.map String.trim)
      |> List.map parseResultCookie
      |> List.fold (fun cookies cookie ->
          cookies |> Map.add cookie.name cookie)
          Map.empty

  let private client_cookie_from (httpCookie : HttpCookie) =
    let ccn = String.Concat [ httpCookie.name; "-client" ]
    { HttpCookie.mkSimple ccn httpCookie.name
        with httpOnly = false
             secure    = httpCookie.secure
             expires   = httpCookie.expires }

  /// Set +relativeExpiry time span on the expiry time of the http cookie
  /// and generate a corresponding client-side cookie with the same expiry, that
  /// has as its data, the cookie name of the http cookie.
  let private slidingExpiry (relativeExpiry : CookieLife) (httpCookie : HttpCookie) =
    let cookieName = httpCookie.name
    let expiry =
      match relativeExpiry with
      | Session -> None
      | MaxAge ts  -> Some (Globals.utc_now().Add ts)
    let httpCookie = { httpCookie with expires = expiry }
    httpCookie, client_cookie_from httpCookie

  let setCookie (cookie : HttpCookie) (ctx : HttpContext) =
    let not_set_cookie : string * string -> bool =
      fst >> (String.eq_ord_ci "Set-Cookie" >> not)
    let cookie_headers =
      ctx.response.cookies
      |> Map.put cookie.name cookie // possibly overwrite
      |> Map.toList
      |> List.map snd // get HttpCookie-s
      |> List.map HttpCookie.toHeader
    let headers' =
      cookie_headers
      |> List.fold (fun headers header ->
          ("Set-Cookie", header) :: headers)
          (ctx.response.headers |> List.filter not_set_cookie)
    { ctx with response = { ctx.response with headers = headers' } }
    |> succeed

  let unsetCookie (cookieName : string) =
    let start_epoch = DateTimeOffset(1970, 1, 1, 0, 0, 1, TimeSpan.Zero) |> Some
    let string_value = HttpCookie.toHeader { HttpCookie.mkSimple cookieName "x" with expires = start_epoch }
    Writers.setHeader "Set-Cookie" string_value

  let setPair (httpCookie : HttpCookie) (clientCookie : HttpCookie) : HttpPart =
    context (fun { runtime = { logger = logger } } ->
      Log.log logger "Suave.Cookie.set_pair" LogLevel.Debug
        (sprintf "setting cookie '%s' len '%d'" httpCookie.name httpCookie.value.Length)
      succeed)
    >>= setCookie httpCookie >>= setCookie clientCookie

  let unsetPair httpCookieName : HttpPart =
    unsetCookie httpCookieName >>= unsetCookie (String.Concat [ httpCookieName; "-client" ])

  type CookiesState =
    { serverKey      : ServerKey
      cookieName     : string
      userStateKey  : string
      relativeExpiry : CookieLife
      secure          : bool }

  [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
  module CookiesState =

    let mk serverKey cookieName userStateKey relativeExpiry secure =
      { serverKey      = serverKey
        cookieName     = cookieName
        userStateKey  = userStateKey
        relativeExpiry = relativeExpiry
        secure          = secure }

  let generateCookies serverKey cookieName relativeExpiry secure plainData =
    let enc, _ = Bytes.cookieEncoding
    match Crypto.secretbox serverKey plainData with
    | Choice1Of2 cookieData ->
      let encodedData = enc cookieData
      { HttpCookie.mkSimple cookieName encodedData
          with httpOnly = true
               secure    = secure }
      |> slidingExpiry relativeExpiry
    | err -> failwithf "internal error on encryption %A" err

  let readCookies key cookieName cookies =
    let _, dec = Bytes.cookieEncoding
    let found =
      cookies
      |> Map.tryFind cookieName
      |> Choice.from_option (NoCookieFound cookieName)
      |> Choice.map (fun c -> c, c |> (HttpCookie.value >> dec))
    match found with
    | Choice1Of2 (cookie, cipher_data) ->
      cipher_data
      |> Crypto.secretboxOpen key
      |> Choice.map_2 DecryptionError
      |> Choice.map (fun plainText -> cookie, plainText)
    | Choice2Of2 x -> Choice2Of2 x

  let refreshCookies relativeExpiry httpCookie : HttpPart =
    slidingExpiry relativeExpiry httpCookie ||> setPair

  let updateCookies (csctx : CookiesState) f_plainText : HttpPart =
    context (fun ctx ->
      let logger = ctx.runtime.logger
      let plainText =
        match readCookies csctx.serverKey csctx.cookieName ctx.response.cookies with
        | Choice1Of2 (_, plain_text) ->
          Log.log logger "Suave.Cookie.updateCookies" LogLevel.Debug "updateCookies - existing"
          f_plainText (Some plain_text)
        | Choice2Of2 _ ->
          Log.log logger "Suave.Cookie.updateCookies" LogLevel.Debug "updateCookies - first time"
          f_plainText None

      /// Since the contents will completely change every write, we simply re-generate the cookie
      generateCookies csctx.serverKey csctx.cookieName
                       csctx.relativeExpiry csctx.secure
                       plainText
      ||> setPair
      >>= Writers.setUserData csctx.userStateKey plainText)

  let cookieState (csctx : CookiesState)
                   // unit -> plain text to store OR something to run of your own!
                   (noCookie : unit -> Choice<byte [], HttpPart>)
                   (decryptionFailure   : _ -> Choice<byte [], HttpPart>)
                   (f_success : HttpPart)
                   : HttpPart =
    context (fun ({ runtime = { logger = logger }} as ctx) ->

      let log = Log.log logger "Suave.Cookie.cookie_state" LogLevel.Debug

      let setCookies plain_text =
        let httpCookie, clientCookie =
          generateCookies csctx.serverKey csctx.cookieName
                           csctx.relativeExpiry csctx.secure
                           plain_text
        setPair httpCookie clientCookie >>=
          Writers.setUserData csctx.userStateKey plain_text

      match readCookies csctx.serverKey csctx.cookieName ctx.request.cookies with
      | Choice1Of2 (httpCookie, plain_text) ->
        log "existing cookie"
        refreshCookies csctx.relativeExpiry httpCookie
          >>= Writers.setUserData csctx.userStateKey plain_text
          >>= f_success

      | Choice2Of2 (NoCookieFound _) ->
        match noCookie () with
        | Choice1Of2 plain_text ->
          log "no existing cookie, setting text"
          setCookies plain_text >>= f_success
        | Choice2Of2 wp_kont ->
          log "no existing cookie, calling app continuation"
          wp_kont

      | Choice2Of2 (DecryptionError err) ->
        log (sprintf "decryption error: %A" err)
        match decryptionFailure err with
        | Choice1Of2 plain_text ->
          log "existing, broken cookie, setting cookie text anew"
          setCookies plain_text >>= f_success
        | Choice2Of2 wp_kont    ->
          log "existing, broken cookie, unsetting it, forwarding to given failure web part"
          wp_kont >>= unsetPair csctx.cookieName)