using System;
using System.Collections.Generic;

namespace Api.Models
{
   public class Dashboard
   {
      public List<Purchase> Purchases
      {
         get;
         set;
      }

      public List<Recommendation> Recommendations
      {
         get;
         set;
      }
   }
}
