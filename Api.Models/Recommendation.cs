using System;

namespace Api.Models
{
   public class Recommendation
   {
      public string BookName
      {
         get;
         set;
      }

      public DateTime DateOfPublication
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
