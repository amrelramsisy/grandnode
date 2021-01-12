﻿using Grand.Core.Configuration;
using Grand.Core.Infrastructure;
using Grand.Core.Infrastructure.DependencyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Grand.Plugin.Payments.CashOnDelivery
{
    public class DependencyRegistrar : IDependencyRegistrar
    {
        public virtual void Register(IServiceCollection builder, ITypeFinder typeFinder, GrandConfig config)
        {
            builder.AddScoped<CashOnDeliveryPaymentProcessor>();
        }

        public int Order
        {
            get { return 10; }
        }
    }

}
