﻿using System.Collections.Generic;
using System.Linq;
using Flurl;
using LtGt;

namespace YtReader.Web {
  public static class YtGtEx {
    public static HtmlElement El(this HtmlDocument e, string selector) => e.QueryElements(selector).FirstOrDefault();
    public static IEnumerable<HtmlElement> Els(this HtmlDocument e, string selector) => e.QueryElements(selector);
    public static string Str(this HtmlElement e, string attributeName) => e.GetAttributeValue(attributeName);
    public static string Str(this QueryParamCollection paramCol, string name) => paramCol.GetAll(name).FirstOrDefault()?.ToString();
    public static string Txt(this HtmlElement e) => e.GetInnerText();
  }
}