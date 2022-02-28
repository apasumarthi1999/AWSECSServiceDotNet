using System;

namespace Api.Models
{
   public class Purchase
   {
      public string BookName
      {
         get;
         set;
      }

      public DateTime DateOfPurchase
      {
         get;
         set;
      }

      public double Price
      {
         get;
         set;
      }
   }
}
